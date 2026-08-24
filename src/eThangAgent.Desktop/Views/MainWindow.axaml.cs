using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Views;

/// <summary>The main window shell: left menu bar plus a tab host with one tab per
///     open agent. 'Open Agent' shows the folder picker; the chosen directory becomes
///     the agent's workspace and opens as a new tab. Opening another directory opens
///     another tab — each backed by its own isolated session container.</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel? _vm;

    public MainWindow() => InitializeComponent();

    public MainWindow(MainViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;

        // The menu button asks the view to show the picker (UI-affine); the result
        // comes back through CompleteOpenAgentAsync on the view-model.
        vm.OpenAgentRequested += async (_, _) => await OpenAgentPickerAsync();
    }

    /// <summary>Shows the native folder picker and opens (or selects) the agent tab.
    ///     A cancelled pick is a no-op. Failures surface as a transcript notice in the
    ///     selected tab, or a dialog when no tab exists yet.</summary>
    private async Task OpenAgentPickerAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the directory this agent will work from",
            AllowMultiple = false,
        });
        if (folders.Count == 0) return; // user cancelled — no-op

        var root = folders[0].Path.LocalPath;
        var result = await _vm!.OpenAgentAsync(root);
        if (!result.IsSuccess)
            await ShowOpenFailedAsync(result.Error!.Message);
    }

    private async Task ShowOpenFailedAsync(string message)
    {
        var dialog = new Window
        {
            Title = "eThang Agent — could not open agent",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(24),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 },
                    new Button { Content = "OK" },
                },
            },
        };
        var okButton = (Button)((StackPanel)dialog.Content!).Children[^1];
        okButton.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private void OnCloseTab(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (sender is Button { DataContext: AgentTabViewModel tab })
            _ = _vm.CloseTabAsync(tab);
    }

    private async void OnWindowClosed(object? sender, EventArgs e)
    {
        try
        {
            // Every open agent completes its root session gracefully on window close.
            var sessions = _vm?.Tabs.Select(t => t.ViewModel).ToList();
            if (sessions is not null && sessions.Count > 0)
                await Task.WhenAll(sessions.Select(s => s.ShutdownAsync()));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
        }
    }
}
