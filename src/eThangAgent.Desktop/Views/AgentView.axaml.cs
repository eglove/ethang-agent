using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Views;

/// <summary>The per-agent chat surface hosted inside one shell tab: transcript,
///     input row with command autocomplete, clarify mode, and status bar. Binds an
///     <see cref="AgentSessionViewModel"/>; every open tab owns one instance, so no
///     state here may be shared or static.</summary>
public partial class AgentView : UserControl
{
    private AgentSessionViewModel? Vm => DataContext as AgentSessionViewModel;

    public AgentView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WireVm();
    }

    private void WireVm()
    {
        var vm = Vm;
        if (vm is null) return;

        // Auto-scroll this agent's transcript as entries arrive (best effort).
        vm.Transcript.Entries.CollectionChanged += (_, _) =>
        {
            try { TranscriptScroll.ScrollToEnd(); } catch { /* layout not ready */ }
        };

        // Animated spinner parity with the terminal frame loop (~12 fps): an 80 ms timer
        // runs only while a turn is busy; Phase transitions reset the displayed state.
        var statusTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        statusTimer.Tick += (_, _) => vm.Status.Tick();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(AgentSessionViewModel.IsBusy)) return;
            if (vm.IsBusy) statusTimer.Start();
            else statusTimer.Stop();
        };

        // Tunnel so Enter/Esc are seen before TextBox class handling consumes them.
        InputBox.AddHandler(KeyDownEvent, OnInputKeyDownTunnel, RoutingStrategies.Tunnel);

        // Drive the command autocomplete from the Text PROPERTY rather than the
        // TextChanged event: property-changed notifications fire for every change
        // source, which TextChanged proved not to do reliably under headless testing.
        InputBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) UpdateCommandPopup();
        };
    }

    private void OnInputKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        var vm = Vm;
        if (vm is null) return;

        if (e.Key == Key.Escape && CommandPopup.IsOpen)
        {
            CommandPopup.IsOpen = false;
            e.Handled = true;
            return;
        }

        // Accept the highlighted (or first) autocomplete suggestion without submitting.
        if (CommandPopup.IsOpen && (e.Key == Key.Tab || e.Key == Key.Enter))
        {
            ViewModels.DesktopCommand? chosen = CommandList.SelectedItem as ViewModels.DesktopCommand;
            if (chosen is null && CommandList.ItemsSource is IEnumerable<ViewModels.DesktopCommand> items)
            {
                chosen = items.FirstOrDefault();
            }
            if (chosen is not null)
            {
                InputBox.Text = chosen.Name;
                InputBox.CaretIndex = InputBox.Text?.Length ?? 0;
            }
            CommandPopup.IsOpen = false;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true; // suppress newline insertion
            var text = InputBox.Text ?? "";
            InputBox.Text = "";
            _ = vm.SubmitAsync(text);
        }
        // Shift+Enter falls through: TextBox inserts the newline.
    }

    private void UpdateCommandPopup()
    {
        var text = InputBox.Text ?? "";
        if (!text.StartsWith('/'))
        {
            CommandPopup.IsOpen = false;
            return;
        }

        var query = text[1..];
        var matches = DesktopCommands.All
            .Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        CommandList.ItemsSource = matches;
        CommandPopup.IsOpen = matches.Count > 0;
    }

    private void OnCommandChosen(object? sender, RoutedEventArgs e)
    {
        CompleteFromSelection();
    }

    private void OnCommandListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Tab)
        {
            CompleteFromSelection();
            e.Handled = true;
        }
    }

    private void CompleteFromSelection()
    {
        if (CommandList.SelectedItem is ViewModels.DesktopCommand chosen)
        {
            InputBox.Text = chosen.Name;
            InputBox.CaretIndex = InputBox.Text?.Length ?? 0;
        }
        CommandPopup.IsOpen = false;
        InputBox.Focus();
    }

    private void OnStopClick(object? sender, RoutedEventArgs e)
    {
        Vm?.RequestStop();
    }

    private async void OnClarifyOption(object? sender, RoutedEventArgs e)
    {
        var vm = Vm;
        if (vm?.Clarify is not { } pending) return;
        if (sender is Button { DataContext: string option })
        {
            var index = pending.Options.ToList().IndexOf(option);
            pending.ChooseOption(index + 1); // 1-based display index
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
        if (Vm is not null) await Vm.WaitForTurnAsync();
    }

    private async void OnClarifyCancel(object? sender, RoutedEventArgs e)
    {
        Vm?.Clarify?.Cancel();
        if (Vm is not null) await Vm.WaitForTurnAsync();
    }
}
