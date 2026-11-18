using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.ToolDomain;
using TextBlock = Avalonia.Controls.TextBlock;

namespace eThangAgent.Desktop.Tests;

/// <summary>The tools window: the page's controls, and the PATH scan starting when the window
///     opens rather than when the app launches.</summary>
[Trait("Category", "Process")]
public class ToolsSettingsWindowTests
{
  [AvaloniaFact]
  public void TheToolsWindowBindsThePageControls()
  {
    ToolsSettingsViewModel vm = new(null, () => new PathToolScan([], 0),
        names => [.. names.Select(name => new PathToolResolution(name, null, null))]);
    ToolsSettingsWindow window = new(vm);
    window.Show();

    Assert.Same(vm, window.DataContext);
    _ = window.GetControl<TextBox>("SearchBox");
    _ = window.GetControl<TextBox>("AddNameBox");
    _ = window.GetControl<Button>("AddNameButton");
    _ = window.GetControl<Button>("SaveButton");
    _ = window.GetControl<ItemsControl>("GroupList");
    _ = window.GetControl<TextBlock>("SummaryText");

    Dispatcher.UIThread.RunJobs();
  }

  /// <summary>The scan is the page's work, not the app's: opening the window is what starts it,
  ///     and a page nobody opened costs nothing.</summary>
  [AvaloniaFact]
  public void ThePathScanStartsWhenTheWindowOpens()
  {
    ToolsSettingsViewModel vm = new(null, () => new PathToolScan([], 0),
        names => [.. names.Select(name => new PathToolResolution(name, null, null))]);
    Assert.False(vm.IsLoading);

    ToolsSettingsWindow window = new(vm);
    window.Show();

    Assert.True(vm.IsLoading);
    Dispatcher.UIThread.RunJobs();
  }
}
