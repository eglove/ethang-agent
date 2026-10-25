using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

  // True only around a programmatic autofill text set: the autofill's own
  // TextChanged must not re-open the popup the Accept just closed; the next
  // user-driven change re-enables the feed.
  private bool _suppressAutocompleteUpdate;

  // ---- Per-row subagent sticky auto-scroll (task 8) ----
  // One wiring per accordion row, mirroring the main chat's pattern above:
  // ScrollChanged feeds the row's controller; the row's Entries CollectionChanged
  // queues a coalesced ScrollToEnd for THAT row's viewer. Attached when the row's
  // Expander materializes (DataContextChanged), released on detach/Dispose.
  private sealed class RowScrollWiring : IDisposable
  {
    private readonly SubagentRowViewModel _row;
    private readonly ScrollViewer _viewer;
    private bool _scrollToEndQueued;

    public RowScrollWiring(SubagentRowViewModel row, ScrollViewer viewer)
    {
      _row = row;
      _viewer = viewer;
      _row.Transcript.Entries.CollectionChanged += OnEntriesChanged;
      viewer.ScrollChanged += OnScrollChanged;
    }

    // Same entry-shape filter as the main chat: appends and streamed tail
    // replaces follow while stuck; other actions never scroll.
    private void OnEntriesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
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

      if (!_row.Transcript.Scroll.ShouldAutoScroll(isUserEntry: false))
      {
        return;
      }

      _scrollToEndQueued = true;
      Dispatcher.UIThread.Post(FlushQueuedScrollToEnd, DispatcherPriority.Loaded);
    }

    // One queued auto-scroll after layout settles; coalesces the burst of
    // entries that can arrive inside one dispatcher turn.
    private void FlushQueuedScrollToEnd()
    {
      if (!_scrollToEndQueued)
      {
        return;
      }

      _scrollToEndQueued = false;
      _viewer.ScrollToEnd();
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
      TranscriptScrollController controller = _row.Transcript.Scroll;
      controller.ObserveScroll(_viewer.Extent.Height, _viewer.Viewport.Height, _viewer.Offset.Y);

      // Geometry drift while pinned (the accordion expanding a sibling row
      // re-lays-out this one) moved the tail out of view: re-pin, coalesced
      // through the same one-shot queue as appends.
      if (controller.ShouldRePin)
      {
        controller.ClearRePin();
        _scrollToEndQueued = true;
        Dispatcher.UIThread.Post(FlushQueuedScrollToEnd, DispatcherPriority.Loaded);
      }
    }

    public void Dispose()
    {
      _row.Transcript.Entries.CollectionChanged -= OnEntriesChanged;
      _viewer.ScrollChanged -= OnScrollChanged;
    }
  }

  // Live wirings, keyed by the row VM whose DataContext materialized them.
  private readonly Dictionary<SubagentRowViewModel, RowScrollWiring> _rowWirings = [];

  /// <summary>Attaches per-row sticky auto-scroll wiring when a row's Expander
  ///     materializes (the ItemsControl realizes the ItemTemplate). Re-attachment
  ///     for the same row replaces the old wiring - a recycled container gets a
  ///     fresh viewer, so the stale subscription must go first.</summary>
  private void OnRowDataContextChanged(object? sender, EventArgs e)
  {
    if (sender is not Expander { DataContext: SubagentRowViewModel row } expander)
    {
      return;
    }

    if (_rowWirings.TryGetValue(row, out RowScrollWiring? existing))
    {
      existing.Dispose();
      _ = _rowWirings.Remove(row);
    }

    // The per-row transcript viewer is the one inside the row Expander
    // (MaxHeight=420). FindControl cannot be used here: this handler fires from
    // DataContext inheritance DURING ContentPresenter.ApplyTemplate (mid layout
    // pass), before the template's name scope registers — FindControl threw
    // 'Could not find parent name scope' and killed the dispatcher (the 2026-10-18
    // spawn crash). Search the visual subtree instead; when the Expander's content
    // has not materialized yet the wiring attaches on the NEXT DataContextChanged
    // (the template re-apply raises inheritance changes again), so a miss is a
    // deferred attach, never a lost one.
    ScrollViewer? viewer = expander.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
    if (viewer is null)
    {
      return;
    }

    _rowWirings.Add(row, new RowScrollWiring(row, viewer));
  }

  /// <summary>Releases every per-row wiring: the panel's rows are about to change
  ///     identity (tab rebuild, VM re-wire) and stale subscriptions must not
  ///     outlive their row.</summary>
  private void ReleaseRowWirings()
  {
    foreach (RowScrollWiring wiring in _rowWirings.Values)
    {
      wiring.Dispose();
    }

    _rowWirings.Clear();
  }

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
      _wiredVm.Workbench.PropertyChanged -= OnWorkbenchPropertyChanged;
      _wiredVm = null;
    }

    // The timers belong to the wiring, not the view: stop the old ones so a re-wire
    // cannot leave a spinner or a subagent refresh running for a detached VM forever.
    _statusTimer?.Stop();
    _subagentTimer?.Stop();

    _restorePending = false;
    ReleaseRowWirings(); // the rows leave with the VM: stale viewer wiring must not outlive them
    AgentSessionViewModel? vm = Vm;
    if (vm is null)
    {
      return;
    }

    _wiredVm = vm;
    vm.Transcript.Entries.CollectionChanged += OnEntriesChanged;
    vm.PropertyChanged += OnVmPropertyChanged;

    // Subagent split view: a 500 ms refresh pulls RUNNING rows' transcripts from the
    // store (the realtime surface). The timer lives on the wiring like the spinner.
    // Contained (the 884fc61 crash fix): the tick's await is async-void — a fault
    // escaping it kills the dispatcher (0xe0434352), so the refresh is wrapped and
    // logged, never fatal.
    _subagentTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
    _subagentTimer.Tick += async (_, _) =>
    {
      try
      {
        await vm.Subagents.RefreshRunningTranscriptsAsync();
      }
#pragma warning disable CA1031 // Named decision: presentation containment — a refresh fault must not take down the dispatcher.
      catch (Exception ex)
      {
        Debug.WriteLine($"subagent transcript refresh fault: {ex}");
      }
#pragma warning restore CA1031
    };
    _subagentTimer.Start();

    // The split: the workbench column takes its width only while the workbench is
    // OPEN (design D5 - permanent, collapsed by the user alone). Row count is no
    // longer the input: the Progress view shows rows, and every other view has none.
    vm.Workbench.PropertyChanged += OnWorkbenchPropertyChanged;
    ApplyWorkbenchLayout(vm.Workbench.IsOpen);

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
    Dispatcher.UIThread.Post(ApplyPendingRestore, DispatcherPriority.Loaded);
  }

  // Spinner timer, recreated per wiring; stops with the VM subscription on re-wire.
  private DispatcherTimer? _statusTimer;

  // Subagent transcript refresh timer, same lifecycle as the spinner timer.
  private DispatcherTimer? _subagentTimer;

  /// <summary>Sizes the workbench column from the workbench's OPEN state (issue #8,
  ///     design D5). The column is a pixel width while open - a GridSplitter resizes a
  ///     column definition, so an Auto column would leave the splitter inert - and zero
  ///     while collapsed, so the chat reclaims the full width.
  ///     <para>This used to key off <see cref="SubagentPanelViewModel.HasRows"/>, which
  ///     was correct when the subagent accordion WAS the right column. Now that the
  ///     workbench is permanent (D5), row count is the wrong input: a session with no
  ///     children would collapse the workbench and hide every view.</para></summary>
  private void OnWorkbenchPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
  {
    if (e.PropertyName == nameof(WorkbenchViewModel.IsOpen) && sender is WorkbenchViewModel workbench)
    {
      ApplyWorkbenchLayout(workbench.IsOpen);
    }
  }

  /// <summary>Workbench width while open. Wide enough for the icon strip plus a
  ///     readable content pane; the splitter adjusts it from here.</summary>
  private const double WorkbenchDefaultWidth = 354;

  /// <summary>The width to restore when the workbench reopens. Collapsing sets the
  ///     column to zero, so the column itself cannot be the memory: reading it back
  ///     would restore 0 and the workbench would never reappear. The splitter's last
  ///     width is captured here instead.</summary>
  private double _workbenchWidth = WorkbenchDefaultWidth;

  private void ApplyWorkbenchLayout(bool isOpen)
  {
    Grid? root = this.FindControl<Grid>("RootSplit");
    if (root is null || root.ColumnDefinitions.Count < 3)
    {
      return;
    }

    // Only a LIVE (non-zero) width is worth remembering: it is what the splitter left.
    GridLength current = root.ColumnDefinitions[2].Width;
    if (current.IsAbsolute && current.Value > 0)
    {
      _workbenchWidth = current.Value;
    }

    root.ColumnDefinitions[2].Width = isOpen ? new GridLength(_workbenchWidth) : new GridLength(0);
  }

  /// <summary>Whether the spinner timer is currently running (test observation surface:
  ///     the attach-to-busy wiring cannot be asserted through the private timer field).</summary>
  internal bool IsSpinnerTimerRunning => _statusTimer?.IsEnabled ?? false;

  /// <summary>Starts/stops the spinner timer with the busy phase.</summary>
  private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
  {
    if (e.PropertyName == nameof(AgentSessionViewModel.IsBusy))
    {
      _statusTimer?.Stop();
      if (sender is AgentSessionViewModel { IsBusy: true })
      {
        _statusTimer?.Start();
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
  ///     real geometry (DispatcherPriority.Loaded = after measure/arrange). The
  ///     controller's move-detector treats the jump as a real move and keeps the
  ///     unstuck state, since the landing spot is away from the bottom.</summary>
  private void ApplyPendingRestore()
  {
    if (Vm is null || !_restorePending)
    {
      return;
    }

    if (TranscriptScroll.Extent.Height <= 0 || TranscriptScroll.Viewport.Height <= 0)
    {
      // Layout has not produced real geometry yet - retry next dispatcher turn.
      Dispatcher.UIThread.Post(ApplyPendingRestore, DispatcherPriority.Loaded);
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

  protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
  {
    base.OnDetachedFromVisualTree(e);
    ReleaseRowWirings(); // recycled containers outlive this view instance
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
      if (vm.Autocomplete is { IsOpen: true } popup)
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
    if (!_suppressAutocompleteUpdate)
    {
      Vm?.UpdateAutocomplete(text);
    }

    bool commandMode = text.TrimStart().StartsWith('!');
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

    // The '/' autocomplete popup owns the keys while open: navigation, Esc,
    // and Enter/Tab completion — Enter NEVER submits while the popup shows a
    // name that still needs completing (spec #28). Any other key falls through
    // to the TextBox's own handling.
    if (vm.Autocomplete is { IsOpen: true } popup && HandlePopupKey(popup, e))
    {
      return;
    }

    if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
    {
      e.Handled = true; // suppress newline insertion
      SubmitInput();
    }
    // Shift+Enter falls through: TextBox inserts the newline.
  }

  /// <summary>Gives the open '/' autocomplete popup first refusal on the key:
  ///     Up/Down navigate, Esc closes, and unshifted Tab/Enter accept the
  ///     selection. Returns true when the popup consumed the key (the caller then
  ///     stops); false means the key falls through to the TextBox.</summary>
  private bool HandlePopupKey(SkillAutocompleteViewModel popup, KeyEventArgs e)
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

    if ((e.Key == Key.Tab || e.Key == Key.Enter) && !shift)
    {
      e.Handled = true; // suppress newline insertion and focus navigation
      HandlePopupAcceptKey(popup, e.Key);
      return true;
    }

    return false;
  }

  /// <summary>Applies the popup's accept key: a name that still needs completing
  ///     autofills the full name plus a trailing space; an already-complete name
  ///     (typed out or previously autofilled, with or without arguments after it)
  ///     submits the input so the session resolves it (spec #28: 'subsequent
  ///     Enter submits and resolves'). With nothing selectable, Enter falls back
  ///     to an ordinary submit (an unknown '/zzz' stays a plain message).</summary>
  private void HandlePopupAcceptKey(SkillAutocompleteViewModel popup, Key key)
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

    string name = popup.Options[popup.SelectedIndex].Name;
    string token = NameTokenOf(InputBox.Text ?? "");
    if (key == Key.Enter && token.Equals(name, StringComparison.OrdinalIgnoreCase))
    {
      popup.Close();
      SubmitInput();
      return;
    }

    SetAutofill(name);
  }

  /// <summary>Accepts a tapped option: same autofill as the keyboard path.</summary>
  private void OnAutocompleteTapped(object? sender, TappedEventArgs e)
  {
    AgentSessionViewModel? vm = Vm;
    if (vm?.Autocomplete is not { IsOpen: true } popup || sender is not ListBox list)
    {
      return;
    }

    if (list.SelectedIndex is int index && index >= 0 && index < popup.Options.Count &&
        popup.Click(index) is { } name)
    {
      SetAutofill(name);
    }
  }

  private void SetAutofill(string name)
  {
    _suppressAutocompleteUpdate = true;
    try
    {
      InputBox.Text = "/" + name + " ";
      InputBox.CaretIndex = InputBox.Text.Length;
    }
    finally
    {
      _suppressAutocompleteUpdate = false;
    }
  }

  private static string NameTokenOf(string input)
  {
    if (!input.StartsWith('/'))
    {
      return "";
    }

    string rest = input[1..];
    int space = rest.IndexOf(' ', StringComparison.Ordinal);
    return space < 0 ? rest : rest[..space];
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

  private void SubmitInput()
  {
    string text = InputBox.Text ?? "";
    InputBox.Text = "";
    _ = Vm?.SubmitAsync(text);
  }

  private void OnStopClick(object? sender, RoutedEventArgs e) => Vm?.RequestStop();

  /// <summary>Collapses the workbench (design D5). The workbench never hides itself on
  ///     content, so this deliberate click is the only thing that closes it; the chat
  ///     then reclaims the full width.</summary>
  private void OnWorkbenchCollapseClick(object? sender, RoutedEventArgs e)
  {
    if (Vm is { } vm)
    {
      vm.Workbench.IsOpen = false;
    }
  }

  /// <summary>Reopens the workbench from the status bar (design D5): the complement of
  ///     <see cref="OnWorkbenchCollapseClick"/>, so collapsing is reversible.</summary>
  private void OnWorkbenchReopenClick(object? sender, RoutedEventArgs e)
  {
    if (Vm is { } vm)
    {
      vm.Workbench.IsOpen = true;
    }
  }

  /// <summary>Workbench icon click (issue #8): selects that view. The view-model ignores
  ///     a view it has not built, so an unavailable icon is inert without the view
  ///     needing to check.</summary>
  private void OnWorkbenchIconClick(object? sender, RoutedEventArgs e)
  {
    if (sender is Button { Tag: WorkbenchView view } && Vm is { } vm)
    {
      vm.Workbench.Select(view);
    }
  }

  /// <summary>Git view change-row click (issue #9): renders that path's diff. The row's
  ///     Tag carries the path a diff can be asked for (a rename's NEW path), so the view
  ///     never has to parse porcelain text.
  ///     <para>Fire-and-forget by design: the read is a git invocation, and blocking the
  ///     click handler on it would freeze the shell for the query's duration.</para></summary>
  private void OnGitChangeClick(object? sender, RoutedEventArgs e)
  {
    if (sender is Button { Tag: string path } && Vm is { } vm)
    {
      _ = vm.Workbench.Git.SelectAsync(path, CancellationToken.None);
    }
  }

  /// <summary>GitHub view PR-row click (issue #10): loads that PR's body into the detail
  ///     area. The row's Tag carries the NUMBER, so the view never parses the display line.
  ///     <para>Fire-and-forget by design: the read is a gh subprocess, and blocking the click
  ///     handler on it would freeze the shell for the call's duration.</para></summary>
  private void OnPullRequestClick(object? sender, RoutedEventArgs e)
  {
    if (sender is Button { Tag: int number } && Vm is { } vm)
    {
      _ = vm.Workbench.PullRequests.SelectAsync(number, CancellationToken.None);
    }
  }

  /// <summary>Opens the create form seeded with the branches already on screen: the
  ///     checked-out branch as HEAD and its base as BASE, so the ordinary case is a title and
  ///     a click. Both come from the statusline's own branch facts rather than a new read.</summary>
  private void OnNewPullRequestClick(object? sender, RoutedEventArgs e)
  {
    if (Vm is not { } vm)
    {
      return;
    }

    vm.Workbench.PullRequests.BeginCreate(vm.Status.Branch, vm.Workbench.Git.SuggestedBaseBranch);
  }

  /// <summary>Sends the create request. Fire-and-forget for the same reason as the PR row:
  ///     the CLI runs a network call, and the form's error slot is what reports a refusal.</summary>
  private void OnSubmitPullRequestClick(object? sender, RoutedEventArgs e)
  {
    if (Vm is { } vm)
    {
      _ = vm.Workbench.PullRequests.SubmitCreateAsync(CancellationToken.None);
    }
  }

  /// <summary>Closes the create form without sending anything.</summary>
  private void OnCancelPullRequestClick(object? sender, RoutedEventArgs e) =>
      Vm?.Workbench.PullRequests.CancelCreate();
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
