using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Views;

/// <summary>The per-agent chat surface hosted inside one shell tab: transcript,
///     input row, clarify mode, and status bar. Binds an
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
    if (_wiredVm is not null)
    {
      _wiredVm.Transcript.Entries.CollectionChanged -= OnEntriesChanged;
      _wiredVm.PropertyChanged -= OnVmPropertyChanged;
      _wiredVm = null;
    }

    // The timer belongs to the wiring, not the view: stop the old one so a re-wire
    // cannot leave a spinner ticking for a detached VM forever.
    _statusTimer?.Stop();

    _restorePending = false;
    AgentSessionViewModel? vm = Vm;
    if (vm is null)
    {
      return;
    }

    _wiredVm = vm;
    vm.Transcript.Entries.CollectionChanged += OnEntriesChanged;
    vm.PropertyChanged += OnVmPropertyChanged;

    // Animated spinner parity with the terminal frame loop (~12 fps): an 80 ms timer
    // runs only while a turn is busy; Phase transitions reset the displayed state.
    _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
    _statusTimer.Tick += (_, _) => vm.Status.Tick();

    // Tunnel so Enter is seen before TextBox class handling consumes it.
    InputBox.AddHandler(KeyDownEvent, OnInputKeyDownTunnel, RoutingStrategies.Tunnel);

    // Tunnel so Esc/End are seen no matter which control inside the view holds
    // focus (input box, transcript, clarify panel).
    AddHandler(KeyDownEvent, OnViewKeyDownTunnel, RoutingStrategies.Tunnel);

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

    if (e.PropertyName == nameof(AgentSessionViewModel.Clarify))
    {
      Dispatcher.UIThread.Post(FocusClarifyPanel);
    }
  }

  private void FocusClarifyPanel()
  {
    if (Vm?.Clarify is null)
    {
      return;
    }

    _ = (ClarifyInput.IsVisible ? ClarifyInput : (Control)ClarifyArea).Focus();
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
    if (vm is null || e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add)
    {
      return;
    }

    bool isUserEntry = e.NewItems is { Count: > 0 } added && added[0] is UserMessageEntry;
    if (!vm.Transcript.Scroll.ShouldAutoScroll(isUserEntry))
    {
      return;
    }

    _scrollToEndQueued = true;
    Dispatcher.UIThread.Post(FlushQueuedScrollToEnd, DispatcherPriority.Loaded);
  }

  /// <summary>Performs one queued auto-scroll after layout settles; coalesces the
  ///     burst of entries that can arrive inside one dispatcher turn.</summary>

  /// <summary>Feeds scroll geometry into the sticky controller.</summary>
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
    Vm?.Transcript.Scroll.ObserveScroll(
        TranscriptScroll.Extent.Height, TranscriptScroll.Viewport.Height, TranscriptScroll.Offset.Y);
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

  private void OnInputKeyDownTunnel(object? sender, KeyEventArgs e)
  {
    AgentSessionViewModel? vm = Vm;
    if (vm is null)
    {
      return;
    }

    if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
    {
      e.Handled = true; // suppress newline insertion
      string text = InputBox.Text ?? "";
      InputBox.Text = "";
      _ = vm.SubmitAsync(text);
    }
    // Shift+Enter falls through: TextBox inserts the newline.
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
      button.Content = "✓";
      Dispatcher.UIThread.Post(() => button.Content = "⧉", DispatcherPriority.Background);
    }
#pragma warning disable CA1031 // Named decision: clipboard copy is best effort; a missing platform clipboard must not crash the shell.
    catch
    {
      // No clipboard available (headless, restricted session) - leave the label as is.
    }
#pragma warning restore CA1031
  }

  private async void OnClarifyOption(object? sender, RoutedEventArgs e)
  {
    AgentSessionViewModel? vm = Vm;
    if (vm?.Clarify is not { } pending)
    {
      return;
    }

    if (sender is Button { DataContext: ClarifyOptionRow row })
    {
      pending.ChooseOption(row.Index); // 1-based display index
    }

    await vm.WaitForTurnAsync();
  }

  /// <summary>Arrow keys move the option highlight; Enter chooses the selection.
  /// Bubbles nothing - the panel owns these keys while a question is pending.</summary>
  private void OnClarifyAreaKeyDown(object? sender, KeyEventArgs e)
  {
    ClarifyViewModel? clarify = Vm?.Clarify;
    if (clarify is null)
    {
      return;
    }

    if (e.Key == Key.Up)
    {
      clarify.MoveSelection(-1);
      e.Handled = true;
    }
    else if (e.Key == Key.Down)
    {
      clarify.MoveSelection(1);
      e.Handled = true;
    }
    else if (e.Key == Key.Enter && (!clarify.AllowFreeText || !ClarifyInput.IsVisible))
    {
      // Free-text Enter falls through to its own KeyDown handler; option questions
      // answer from the keyboard selection.
      clarify.ChooseSelected();
      _ = Vm!.WaitForTurnAsync();
      e.Handled = true;
    }
  }

  private void OnClarifyInputKeyDown(object? sender, KeyEventArgs e)
  {
    if (e.Key == Key.Enter)
    {
      e.Handled = true;
      Vm?.Clarify?.SubmitFreeText();
    }
  }

  private async void OnClarifyAnswer(object? sender, RoutedEventArgs e)
  {
    Vm?.Clarify?.SubmitFreeText();
    if (Vm is not null)
    {
      await Vm.WaitForTurnAsync();
    }
  }

  private async void OnClarifyCancel(object? sender, RoutedEventArgs e)
  {
    Vm?.Clarify?.Cancel();
    if (Vm is not null)
    {
      await Vm.WaitForTurnAsync();
    }
  }
}
