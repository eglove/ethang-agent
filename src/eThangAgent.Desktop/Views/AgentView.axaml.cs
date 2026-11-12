using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Views;

/// <summary>The per-agent chat surface hosted inside one shell tab: transcript,
///     input row, and status bar. Binds an
///     <see cref="AgentSessionViewModel"/>; every open tab owns one instance, so no
///     state here may be shared or static. Auto-scroll is sticky: agent-voice
///     entries follow the tail only while the user rests at the bottom; a user
///     entry never scrolls; End re-sticks; scroll state lives on the transcript
///     controller, so a rebuilt view (tab switch) inherits it.</summary>
internal partial class AgentView : UserControl
{
  private AgentSessionViewModel? Vm => DataContext as AgentSessionViewModel;

  // The VM whose events are currently subscribed. A view instance may see several
  // DataContexts (TabControl recycles controls); wiring follows DataContext, and
  // the previous VM is always unsubscribed first - no orphaned handlers.
  private AgentSessionViewModel? _wiredVm;

  // One-shot pending auto-scroll, posted to the dispatcher at Loaded priority -
  // extent is stale inside CollectionChanged, so the scroll must run after layout.
  private bool _scrollToEndQueued;

  // Reading-position restore (tab switch): captured at attach - before the fresh
  // view's own layout events can clobber the controller - as the offset to restore,
  // or null for "re-pin to bottom" (the transcript was stuck when detached).
  private bool _restorePending;
  private double _restoreOffset;

  // True only around a programmatic commit text set: the commit's own TextChanged
  // must not re-open the popup the Accept just closed; the next user-driven change
  // re-enables the feed.
  private bool _suppressReferenceFeed;

  public AgentView()
  {
    InitializeComponent();
    // Keyboard surface: focusable so End (scroll to bottom) reaches the view's
    // tunnel when the user has clicked into empty transcript space, not a TextBox.
    Focusable = true;
    DataContextChanged += OnDataContextChanged;
  }

  /// <summary>Re-wires event subscriptions to the incoming DataContext,
  ///     unsubscribing the previous VM first (the tab-rebuild leak fix).</summary>
  private void OnDataContextChanged(object? sender, EventArgs e)
  {
    RemoveHandler(DragDrop.DropEvent, OnImageDrop);
    DragDrop.SetAllowDrop(this, false);
    if (_wiredVm is not null)
    {
      _wiredVm.Transcript.Entries.CollectionChanged -= OnEntriesChanged;
      _wiredVm.PropertyChanged -= OnVmPropertyChanged;
      _wiredVm = null;
    }

    // The spinner timer belongs to the wiring, not the view: stop the old one so a re-wire
    // cannot leave a spinner running for a detached VM forever.
    _statusTimer?.Stop();

    _restorePending = false;
    TranscriptScroll.LayoutUpdated -= OnTranscriptLayoutUpdated;
    AgentSessionViewModel? vm = Vm;
    if (vm is null)
    {
      return;
    }

    _wiredVm = vm;
    vm.Transcript.Entries.CollectionChanged += OnEntriesChanged;
    vm.PropertyChanged += OnVmPropertyChanged;

    // Issue #307 D6: the workbench rail, the splitter that sizes it, and the subagent
    // accordion all moved to WorkspaceTabView. A cell is its own chat and nothing else,
    // which is what lets every cell in a grid carry one.

    // Animated spinner parity with the terminal frame loop (~12 fps): an 80 ms timer
    // runs only while a turn is busy; Phase transitions reset the displayed state.
    _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
    _statusTimer.Tick += (_, _) =>
    {
      vm.Status.Tick();
      vm.Transcript.TickToolElapsed();
    };

    // A turn may already be in flight when this view attaches (tab switch back to a
    // busy workspace, or tab content materializing mid-turn): the IsBusy PropertyChanged
    // event fired before this subscription existed, so the attach path must consult the
    // VM's current state or the spinner freezes with the phase label showing.
    if (vm.IsBusy)
    {
      _statusTimer.Start();
    }

    // Tunnel so Enter is seen before TextBox class handling consumes it.
    InputBox.AddHandler(KeyDownEvent, OnInputKeyDownTunnel, RoutingStrategies.Tunnel);

    // Command-mode affordance: live as the user types — an input starting with '!'
    // gets the amber command border and the shell watermark; clearing reverts.
    InputBox.TextChanged += OnInputTextChanged;

    // The caret is the popup's second input: moving it into or out of a token opens or
    // closes the popup without any text changing. TextBox exposes no selection event, so the
    // caret and selection properties are observed directly.
    InputBox.PropertyChanged += OnInputPropertyChanged;

    // The first '@' needs a file cache to search, so the enumeration starts when the input
    // takes focus rather than at the first keystroke.
    InputBox.GotFocus += OnInputGotFocus;

    // Tunnel so Esc/End are seen no matter which control inside the view holds
    // focus (input box, transcript).
    AddHandler(KeyDownEvent, OnViewKeyDownTunnel, RoutingStrategies.Tunnel);

    // Paste (issue #20): Ctrl+V on the input box attaches a clipboard image; plain
    // text paste falls through to the TextBox's own handling.
    InputBox.AddHandler(KeyDownEvent, OnInputPasteKey, RoutingStrategies.Tunnel);
    // Drop (issue #20): image files dropped anywhere on the view stage as attachments.
    DragDrop.SetAllowDrop(this, true);
    AddHandler(DragDrop.DropEvent, OnImageDrop);

    // Reading-position restore: a transcript left unstuck elsewhere (tab switch)
    // gets its reading offset back; a stuck one re-pins to the bottom - both after
    // this fresh view's first real layout. The target is captured NOW, at attach,
    // because the view's own initial layout pass would otherwise overwrite the
    // controller's offset with the fresh-view zero before the restore reads it.
    _restorePending = true;
    _restoreOffset = vm.Transcript.Scroll.StuckToBottom ? -1 : vm.Transcript.Scroll.LastOffset;
    TranscriptScroll.LayoutUpdated += OnTranscriptLayoutUpdated;
    Dispatcher.UIThread.Post(ApplyPendingRestore, DispatcherPriority.Loaded);
  }

  // Spinner timer, recreated per wiring; stops with the VM subscription on re-wire.
  private DispatcherTimer? _statusTimer;

  /// <summary>Whether the spinner timer is currently running (test observation surface:
  ///     the attach-to-busy wiring cannot be asserted through the private timer field).</summary>
  internal bool IsSpinnerTimerRunning => _statusTimer?.IsEnabled ?? false;

  /// <summary>Starts/stops the spinner timer with the busy phase, and marks the composer's
  ///     cost readout amber once the message's references pass the high-token threshold.</summary>
  private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
  {
    if (e.PropertyName == nameof(AgentSessionViewModel.IsBusy))
    {
      _statusTimer?.Stop();
      if (sender is AgentSessionViewModel { IsBusy: true })
      {
        _statusTimer?.Start();
      }

      return;
    }

    // The amber class follows the flag, and the flag is re-read on every cost change: a
    // label that appears while the flag is already true still needs the class applied.
    if (e.PropertyName is nameof(AgentSessionViewModel.ReferenceCostIsHigh)
        or nameof(AgentSessionViewModel.ReferenceCostLabel))
    {
      if (sender is AgentSessionViewModel { ReferenceCostIsHigh: true })
      {
        ReferenceCostText.Classes.Add("amber");
      }
      else
      {
        _ = ReferenceCostText.Classes.Remove("amber");
      }
    }
  }

  /// <summary>Sticky auto-scroll: entries arriving while the user rests at the
  ///     bottom (and the entry is not user-voice) scroll the tail into view. The
  ///     actual scroll defers to LayoutUpdated - extent is stale inside
  ///     CollectionChanged - and runs once per queued request.
  ///     <para>Thread contract: the turn pipeline can add entries from its own thread,
  ///     so this handler runs on EITHER thread. It must therefore touch only the
  ///     captured <see cref="_wiredVm"/> field (a plain reference) and dispatcher-safe
  ///     calls — reading the DataContext property would throw cross-thread (its
  ///     getter goes through Avalonia's VerifyAccess).</para></summary>
  private void OnEntriesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
  {
    AgentSessionViewModel? vm = _wiredVm;
    if (vm is null)
    {
      return;
    }

    // Appends ADD entries; streamed deltas REPLACE the tail record while its block is
    // open. Both keep the tail in view while stuck - a stream must scroll as it
    // grows, not snap when the block closes. User-voice appends never scroll.
    if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
    {
      if (e.NewItems is { Count: > 0 } added && added[0] is UserMessageEntry)
      {
        return;
      }
    }
    else if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Replace)
    {
      return;
    }

    if (!vm.Transcript.Scroll.ShouldAutoScroll(isUserEntry: false))
    {
      return;
    }

    _scrollToEndQueued = true;
    Dispatcher.UIThread.Post(FlushQueuedScrollToEnd, DispatcherPriority.Loaded);
  }

  /// <summary>Performs one queued auto-scroll after layout settles; coalesces the
  ///     burst of entries that can arrive inside one dispatcher turn.</summary>
  private void FlushQueuedScrollToEnd()
  {
    if (!_scrollToEndQueued)
    {
      return;
    }

    _scrollToEndQueued = false;
    TranscriptScroll.ScrollToEnd();
  }

  /// <summary>Re-attempts the pending reading-position restore after a layout pass, which is
  ///     the only event that can turn zero geometry into real geometry.</summary>
  private void OnTranscriptLayoutUpdated(object? sender, EventArgs e) => ApplyPendingRestore();

  private void OnTranscriptScrollChanged(object? sender, ScrollChangedEventArgs e)
  {
    TranscriptScrollController? controller = Vm?.Transcript.Scroll;
    if (controller is null)
    {
      return;
    }

    controller.ObserveScroll(
        TranscriptScroll.Extent.Height, TranscriptScroll.Viewport.Height, TranscriptScroll.Offset.Y);

    // Geometry drift while pinned (resize, refocus re-layout) moved the tail out of
    // view: re-pin after layout, coalesced through the same one-shot queue as appends.
    if (controller.ShouldRePin)
    {
      controller.ClearRePin();
      _scrollToEndQueued = true;
      Dispatcher.UIThread.Post(FlushQueuedScrollToEnd, DispatcherPriority.Loaded);
    }
  }

  /// <summary>Applies the transcript's saved reading offset once layout has produced
  ///     real geometry. The controller's move-detector treats the jump as a real move and
  ///     keeps the unstuck state, since the landing spot is away from the bottom.
  ///     <para>GEOMETRY THAT NEVER ARRIVES IS NOT RETRIED HERE: a transcript laid out at zero
  ///     height (a cell in a grid that never gave it a row, a detached view) keeps
  ///     <c>Viewport.Height</c> at zero for as long as that holds, so a re-post from this
  ///     method turns the dispatcher into a spin. The attempt is driven by
  ///     <see cref="OnTranscriptLayoutUpdated"/> instead, so it runs exactly when there is
  ///     new geometry to read and costs nothing when there is not.</para></summary>
  private void ApplyPendingRestore()
  {
    if (Vm is null || !_restorePending)
    {
      return;
    }

    if (TranscriptScroll.Extent.Height <= 0 || TranscriptScroll.Viewport.Height <= 0)
    {
      return;
    }

    _restorePending = false;
    if (TranscriptScroll.Extent.Height <= TranscriptScroll.Viewport.Height)
    {
      return; // content fits; nothing to restore
    }

    if (_restoreOffset < 0)
    {
      TranscriptScroll.ScrollToEnd(); // stuck when detached: re-pin to the bottom
      return;
    }

    TranscriptScroll.Offset = TranscriptScroll.Offset.WithY(_restoreOffset);
  }

  /// <summary>Esc stops the active turn - same path as the Stop button - whenever
  ///     any control inside this view holds keyboard focus. End re-sticks the
  ///     transcript to the bottom unless the key originated in a text box, where
  ///     End belongs to the caret.</summary>
  private void OnViewKeyDownTunnel(object? sender, KeyEventArgs e)
  {
    AgentSessionViewModel? vm = Vm;
    if (vm is null)
    {
      return;
    }

    if (e.Key == Key.Escape)
    {
      // The popup outranks the stop shortcut: Esc closes it first (the input
      // box's own tunnel would do this, but the view tunnel runs first in the
      // tunnel route, so the guard lives here too).
      if (vm.ReferenceCompletions is { IsOpen: true } popup)
      {
        e.Handled = true;
        popup.Close();
        return;
      }

      if (vm.IsBusy)
      {
        e.Handled = true;
        vm.RequestStop();
      }

      return;
    }

    if (e.Key == Key.End && e.Source is not TextBox)
    {
      e.Handled = true;
      vm.Transcript.Scroll.RequestScrollToEnd();
      TranscriptScroll.ScrollToEnd();
    }
  }

  /// <summary>Toggles command mode with the input text: text starting with '!' (after
  ///     trim) shows the amber border and shell watermark; anything else reverts.
  ///     Pure view concern — the transcript's sticky state is untouched.</summary>
  private void OnInputTextChanged(object? sender, TextChangedEventArgs e)
  {
    string text = InputBox.Text ?? "";
    if (!_suppressReferenceFeed)
    {
      Vm?.UpdateReferenceCompletions(text, InputBox.CaretIndex);
      _ = Vm?.UpdateReferenceCostAsync(text);
    }

    bool commandMode = text.TrimStart().StartsWith('!', StringComparison.Ordinal);
    if (commandMode)
    {
      InputBox.Classes.Add("command");
    }
    else
    {
      _ = InputBox.Classes.Remove("command");
    }

    InputBox.PlaceholderText = commandMode ? "Run a shell command (!)" : "Type a message";
  }

  private void OnInputKeyDownTunnel(object? sender, KeyEventArgs e)
  {
    AgentSessionViewModel? vm = Vm;
    if (vm is null)
    {
      return;
    }

    // The reference popup owns the keys while open: navigation, Esc, and Tab completion.
    // Any other key falls through to the TextBox's own handling.
    if (vm.ReferenceCompletions is { IsOpen: true } popup && HandlePopupKey(popup, e))
    {
      return;
    }

    // Enter submits only while no popup shows (spec 6): with the popup open, Enter either
    // completes the token or is consumed by the popup path above.
    if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
    {
      e.Handled = true; // suppress newline insertion
      SubmitInput();
    }
    // Shift+Enter falls through: TextBox inserts the newline.
  }

  /// <summary>Gives the open reference popup first refusal on the key: Up/Down navigate, Esc
  ///     closes, and unshifted Tab accepts the selection. Returns true when the popup consumed
  ///     the key (the caller then stops); false means the key falls through to the TextBox.</summary>
  private bool HandlePopupKey(ComposerReferenceViewModel popup, KeyEventArgs e)
  {
    bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
    if (e.Key == Key.Down)
    {
      e.Handled = true;
      popup.MoveDown();
      return true;
    }

    if (e.Key == Key.Up)
    {
      e.Handled = true;
      popup.MoveUp();
      return true;
    }

    if (e.Key == Key.Escape)
    {
      e.Handled = true;
      popup.Close();
      return true;
    }

    // Tab always completes; Enter completes only while the token still needs completing,
    // so a fully typed name followed by Enter sends (spec 6).
    if ((e.Key == Key.Tab || e.Key == Key.Enter) && !shift)
    {
      e.Handled = true; // suppress newline insertion and focus navigation
      HandlePopupAcceptKey(popup, e.Key);
      return true;
    }

    return false;
  }

  /// <summary>Applies the popup's accept key: Tab and Enter both commit the highlighted option
  ///     over the active token, and neither sends — an open popup means the line is not sent yet
  ///     (spec 6). With nothing selectable, Enter falls back to an ordinary submit (an unknown
  ///     '/zzz' stays a plain message).</summary>
  private void HandlePopupAcceptKey(ComposerReferenceViewModel popup, Key key)
  {
    if (popup.SelectedIndex < 0 || popup.SelectedIndex >= popup.Options.Count)
    {
      if (key == Key.Enter)
      {
        popup.Close();
        SubmitInput();
      }

      return;
    }

    if (popup.Accept() is { } commit)
    {
      ApplyCommit(commit);
    }
  }

  /// <summary>Accepts a tapped option: same commit as the keyboard path.</summary>
  private void OnReferenceCompletionsTapped(object? sender, TappedEventArgs e)
  {
    AgentSessionViewModel? vm = Vm;
    if (vm?.ReferenceCompletions is not { IsOpen: true } popup || sender is not ListBox list)
    {
      return;
    }

    if (list.SelectedIndex is int index && popup.Click(index) is { } commit)
    {
      ApplyCommit(commit);
    }
  }

  /// <summary>Writes the commit's replacement over the token it names, then places the caret
  ///     after it. The programmatic text set is suppressed, and the popup is fed ONCE with the
  ///     committed state: the commit's own trailing space leaves the caret outside every token,
  ///     so the popup stays closed for the token just committed.</summary>
  private void ApplyCommit(ReferenceCommit commit)
  {
    _suppressReferenceFeed = true;
    try
    {
      string text = InputBox.Text ?? "";
      int start = Math.Clamp(commit.Start, 0, text.Length);
      int length = Math.Clamp(commit.Length, 0, text.Length - start);
      string updated = string.Concat(text.AsSpan(0, start), commit.InsertText, text.AsSpan(start + length));
      InputBox.Text = updated;
      InputBox.CaretIndex = Math.Clamp(commit.CaretIndex, 0, updated.Length);
      Vm?.UpdateReferenceCompletions(updated, InputBox.CaretIndex);
      _ = Vm?.UpdateReferenceCostAsync(updated);
    }
    finally
    {
      _suppressReferenceFeed = false;
    }
  }

  /// <summary>Feeds the popup from the caret: a move into a token opens it and a move out of
  ///     one closes it, with no text change involved.</summary>
  private void OnInputPropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
  {
    if (_suppressReferenceFeed
        || (e.Property != TextBox.CaretIndexProperty
            && e.Property != TextBox.SelectionStartProperty
            && e.Property != TextBox.SelectionEndProperty))
    {
      return;
    }

    Vm?.UpdateReferenceCompletions(InputBox.Text ?? "", InputBox.CaretIndex);
  }

  /// <summary>Starts the workspace file enumeration the popup searches, so the first <c>@</c>
  ///     already has a cache.</summary>
  private void OnInputGotFocus(object? sender, FocusChangedEventArgs e) =>
      _ = Vm?.RefreshReferenceIndexAsync();

  /// <summary>Accepts the highlighted option through the same path the Tab key takes. The test
  ///     observation surface: the key route needs a focused window, the commit does not.</summary>
  internal void AcceptCompletionsForTest()
  {
    if (Vm?.ReferenceCompletions is { IsOpen: true } popup)
    {
      HandlePopupAcceptKey(popup, Key.Tab);
    }
  }

  /// <summary>Ctrl+V on the input box: a clipboard IMAGE stages as an attachment
  ///     (issue #20); a text clipboard falls through so normal paste still works.
  ///     The bitmap re-encodes as PNG - the clipboard carries pixels, not files.</summary>
  private async void OnInputPasteKey(object? sender, KeyEventArgs e)
  {
    AgentSessionViewModel? vm = Vm;
    TopLevel? top = TopLevel.GetTopLevel(this);
    if (vm is null || top?.Clipboard is not { } clipboard || e.Key != Key.V
        || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
    {
      return;
    }

    try
    {
      using Avalonia.Media.Imaging.Bitmap? bitmap =
          await Avalonia.Input.Platform.ClipboardExtensions.TryGetBitmapAsync(clipboard).ConfigureAwait(true);
      if (bitmap is null)
      {
        return; // text (or nothing) on the clipboard: normal paste proceeds
      }

      e.Handled = true;
      using MemoryStream ms = new();
      bitmap.Save(ms, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
      _ = vm.AttachImageBytes(ms.ToArray(), "clipboard.png");
    }
#pragma warning disable CA1031 // Named decision: clipboard probing is best effort; a hostile clipboard shape must not crash the shell.
    catch
    {
      // Unreadable clipboard: leave e.Handled false so text paste still works.
    }
#pragma warning restore CA1031
  }

  /// <summary>Image files dropped on the view stage as attachments (issue #20);
  ///     non-image files are rejected by the staging surface with a notice.</summary>
  private void OnImageDrop(object? sender, DragEventArgs e)
  {
    AgentSessionViewModel? vm = Vm;
    if (vm is null)
    {
      return;
    }

    if (e.DataTransfer.TryGetFiles() is not { } files)
    {
      return;
    }

    foreach (IStorageItem item in files)
    {
      if (item.Path is { } path)
      {
        _ = vm.AttachImageFile(path.LocalPath);
      }
    }
  }

  /// <summary>Removes one staged image from the chip row.</summary>
  private void OnRemovePendingImage(object? sender, RoutedEventArgs e)
  {
    if (sender is Button { Tag: PendingImage pending } && Vm is { } vm)
    {
      int index = vm.PendingImages.IndexOf(pending);
      vm.RemovePendingImage(index);
    }
  }

  /// <summary>Sends the input and clears the box. A reference that resolves to nothing is
  ///     reported as a notice and the message still goes, so the box is cleared either way and
  ///     only a fault puts the text back.</summary>
  private void SubmitInput() => _ = SubmitAndRestoreAsync();

  /// <summary>Sends the input and restores it when the send faulted. A fault is delivered
  ///     to the transcript rather than dropped: the caller fire-and-forgets this task, so an
  ///     unobserved exception would leave the user with no sign the message never went.</summary>
  private async Task SubmitAndRestoreAsync()
  {
    AgentSessionViewModel? vm = Vm;
    string text = InputBox.Text ?? "";
    InputBox.Text = "";
    if (vm is null)
    {
      return;
    }

    try
    {
      await vm.SubmitAsync(text).ConfigureAwait(true);
    }
#pragma warning disable CA1031 // Named decision: the submit path is a fault boundary.
    catch (Exception ex)
    {
      vm.Transcript.AddNotice($"Error [SubmitFault]: {ex.Message}");
      InputBox.Text = text;
      InputBox.CaretIndex = text.Length;
    }
#pragma warning restore CA1031
  }

  private void OnStopClick(object? sender, RoutedEventArgs e) => Vm?.RequestStop();

  /// <summary>Copies the full session id to the clipboard; the button label flashes
  ///     a checkmark and reverts shortly after (best effort - no clipboard, no crash).</summary>
  private async void OnCopySessionId(object? sender, RoutedEventArgs e)
  {
    AgentSessionViewModel? vm = Vm;
    if (sender is not Button button || vm is null)
    {
      return;
    }

    try
    {
      if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
      {
        return;
      }

      await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, vm.SessionId.ToString());
      button.Content = SessionIdStatusLabel.Success;
      Dispatcher.UIThread.Post(() => button.Content = SessionIdStatusLabel.Default, DispatcherPriority.Background);
    }
#pragma warning disable CA1031 // Named decision: clipboard copy is best effort; a missing platform clipboard must not crash the shell.
    catch
    {
      // No clipboard available (headless, restricted session) - leave the label as is.
    }
#pragma warning restore CA1031
  }
}
