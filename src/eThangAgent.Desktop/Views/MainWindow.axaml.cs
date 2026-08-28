using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Views;

/// <summary>The main window shell: left menu bar plus a tab host with one tab per
///     open agent. 'Open Agent' shows the new-agent dialog (provider dropdown plus
///     workspace picker); the chosen pair opens as a new tab wired exclusively for
///     that provider. Opening another directory (or the same directory under the
///     other provider) opens another tab — each backed by its own isolated session
///     container.</summary>
internal partial class MainWindow : Window
{
  private readonly MainViewModel? _vm;

  public MainWindow() => InitializeComponent();

  public MainWindow(MainViewModel vm) : this()
  {
    ArgumentNullException.ThrowIfNull(vm);
    _vm = vm;
    DataContext = vm;

    // The menu button asks the view to show the new-agent modal (UI-affine); the
    // chosen provider/workspace pair comes back through OpenAgentAsync. The gear
    // button shows the settings modal the same way; its result flows through
    // ApplySettingsAsync. The Model button shows the selected tab's model picker;
    // its result flows through ApplyModelChoiceAsync.
    vm.OpenAgentRequested += async (_, _) => await ShowNewAgentDialogAsync();
    vm.SettingsRequested += async (_, _) => await ShowSettingsDialogAsync();
    vm.ModelPickerRequested += async (_, _) => await ShowModelPickerDialogAsync();
  }

  /// <summary>Shows the new-agent dialog (provider dropdown + workspace picker) and
  ///     opens (or selects) the agent tab. A cancelled dialog is a no-op. Failures
  ///     surface as a dialog.</summary>
  private async Task ShowNewAgentDialogAsync()
  {
    NewAgentWindow dialog = new(_vm!.AvailableProviders, _vm.PreferredProviderId);
    NewAgentChoice? choice = await dialog.ShowDialog<NewAgentChoice?>(this);
    if (choice is null)
    {
      return; // user cancelled — no-op
    }

    Result<AgentTabViewModel> result = await _vm.OpenAgentAsync(choice.WorkspaceRoot, choice.ProviderId);
    if (!result.IsSuccess)
    {
      await ShowOpenFailedAsync(result.Error!.Message);
    }
  }

  /// <summary>Shows the settings modal prefilled with the current keys. A cancelled
  ///     dialog is a no-op; a confirmed one applies the keys (persist + factory rebind)
  ///     on the shell.</summary>
  private async Task ShowSettingsDialogAsync()
  {
    SettingsWindow dialog = new(_vm!.ConfiguredOpenRouterKey, _vm.ConfiguredZaiKey);
    SettingsUpdate? update = await dialog.ShowDialog<SettingsUpdate?>(this);
    if (update is null)
    {
      return; // user cancelled — no-op
    }

    await _vm.ApplySettingsAsync(update);
  }

  /// <summary>Shows the model picker for the selected tab. A cancelled dialog is a
  ///     no-op; a confirmed one applies the choice on the shell (session + per-workspace
  ///     preference). Only OpenRouter offers the auto row — z.ai has no automatic
  ///     resolution, so its picker is just the static lineup.</summary>
  private async Task ShowModelPickerDialogAsync()
  {
    Func<CancellationToken, Task<Result<IReadOnlyList<ModelProviderEntry>>>>? loader =
        _vm!.SelectedTabCatalogLoader;
    if (loader is null || _vm.SelectedTab is not { } tab)
    {
      return; // no selected tab — the menu entry is hidden anyway
    }

    bool allowAuto = string.Equals(
        tab.Container.ProviderName, Providers.OpenRouter, StringComparison.Ordinal);
    ModelPickerWindow dialog = new(loader, allowAuto, tab.Container.Preferences?.ModelId);
    ModelChoice? choice = await dialog.ShowDialog<ModelChoice?>(this);
    if (choice is null)
    {
      return; // user cancelled — no-op
    }

    await _vm.ApplyModelChoiceAsync(choice.ModelId);
  }

  private async Task ShowOpenFailedAsync(string message)
  {
    Window dialog = new()
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
    Button okButton = (Button)((StackPanel)dialog.Content).Children[^1];
    okButton.Click += (_, _) => dialog.Close();
    await dialog.ShowDialog(this);
  }

  private void OnCloseTab(object? sender, RoutedEventArgs e)
  {
    if (_vm is null)
    {
      return;
    }

    if (sender is Button { DataContext: AgentTabViewModel tab })
    {
      _ = _vm.CloseTabAsync(tab);
    }
  }

  private async void OnWindowClosed(object? sender, EventArgs e)
  {
    try
    {
      // Every open agent completes its root session gracefully on window close.
      List<AgentSessionViewModel>? sessions = _vm?.Tabs.Select(t => t.ViewModel).ToList();
      if (sessions is not null && sessions.Count > 0)
      {
        await Task.WhenAll(sessions.Select(s => s.ShutdownAsync()));
      }
    }
    // Named decision (CA1031): shutdown on close is best effort — a failing
    // persistence teardown must never prevent the window from closing.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception ex)
    {
      await Console.Error.WriteLineAsync(ex.ToString());
    }
#pragma warning restore CA1031
  }
}
