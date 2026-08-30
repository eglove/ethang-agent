using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Views;

/// <summary>The per-agent chat surface hosted inside one shell tab: transcript,
///     input row, clarify mode, and status bar. Binds an
///     <see cref="AgentSessionViewModel"/>; every open tab owns one instance, so no
///     state here may be shared or static.</summary>
internal partial class AgentView : UserControl
{
  private AgentSessionViewModel? Vm => DataContext as AgentSessionViewModel;

  public AgentView()
  {
    InitializeComponent();
    DataContextChanged += (_, _) => WireVm();
  }

  private void WireVm()
  {
    AgentSessionViewModel? vm = Vm;
    if (vm is null)
    {
      return;
    }

    // Auto-scroll this agent's transcript as entries arrive (best effort).
    vm.Transcript.Entries.CollectionChanged += (_, _) =>
    {
      try
      {
        TranscriptScroll.ScrollToEnd();
      }
      // Named decision (CA1031): scroll is best effort while layout settles.
#pragma warning disable CA1031 // Do not catch general exception types
      catch { /* layout not ready */ }
#pragma warning restore CA1031
    };

    // Animated spinner parity with the terminal frame loop (~12 fps): an 80 ms timer
    // runs only while a turn is busy; Phase transitions reset the displayed state.
    DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    statusTimer.Tick += (_, _) => vm.Status.Tick();
    vm.PropertyChanged += (_, e) =>
    {
      if (e.PropertyName != nameof(AgentSessionViewModel.IsBusy))
      {
        return;
      }

      if (vm.IsBusy)
      {
        statusTimer.Start();
      }
      else
      {
        statusTimer.Stop();
      }
    };

    // Tunnel so Enter is seen before TextBox class handling consumes it.
    InputBox.AddHandler(KeyDownEvent, OnInputKeyDownTunnel, RoutingStrategies.Tunnel);

    // Tunnel so Esc is seen no matter which control inside the view holds focus
    // (input box, transcript, clarify panel): while a turn runs it stops it, exactly
    // like the Stop button. Idle Esc falls through — no notice, no interruption.
    this.AddHandler(KeyDownEvent, OnViewKeyDownTunnel, RoutingStrategies.Tunnel);

    // When a clarify question surfaces, move keyboard focus into the clarify panel so
    // arrow keys + Enter work immediately (free-text questions land in the text box).
    vm.PropertyChanged += (_, e) =>
    {
      if (e.PropertyName == nameof(AgentSessionViewModel.Clarify))
      {
        Dispatcher.UIThread.Post(FocusClarifyPanel);
      }
    };
  }

  private void FocusClarifyPanel()
  {
    if (Vm?.Clarify is null)
    {
      return;
    }

    _ = (ClarifyInput.IsVisible ? ClarifyInput : (Control)ClarifyArea).Focus();
  }

  /// <summary>Arrow keys move the option highlight; Enter chooses the selection.
  /// Bubbles nothing — the panel owns these keys while a question is pending.</summary>
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



  /// <summary>Esc stops the active turn — same path as the Stop button — whenever any
  /// control inside this view holds keyboard focus.</summary>
  private void OnViewKeyDownTunnel(object? sender, KeyEventArgs e)
  {
    AgentSessionViewModel? vm = Vm;
    if (e.Key != Key.Escape || vm is null || !vm.IsBusy)
    {
      return;
    }

    e.Handled = true;
    vm.RequestStop();
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
