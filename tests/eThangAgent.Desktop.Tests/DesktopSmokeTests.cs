using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

[assembly: AvaloniaTestApplication(typeof(eThangAgent.Desktop.Tests.TestApp))]

namespace eThangAgent.Desktop.Tests;

// Named decision (CA1515): Avalonia headless discovers the app type via the
// assembly attribute; keep it public for reflection and static for the holder rule.
#pragma warning disable CA1515 // Types can be made internal
public static class TestApp
{
  internal static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
      .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
#pragma warning restore CA1515

[Trait("Category", "Process")]
public class DesktopSmokeTests
{
  [AvaloniaFact]
  public void MainWindow_Instantiates_And_Has_Title()
  {
    MainWindow window = new();
    Assert.Equal("eThang Agent", window.Title);
  }

  /// <summary>The app's first window is the launcher, and it carries the three things the
  ///     launcher exists for: the recent list, an open-folder action, and Settings.</summary>
  [AvaloniaFact]
  public void Launcher_Instantiates_WithItsThreeActions()
  {
    LauncherWindow window = new(new LauncherViewModel(new FakeRecentWorkspaceStore(),
        hasConfiguredProvider: false));
    window.Show();

    Assert.Equal("eThang Agent", window.Title);
    Assert.NotNull(window.GetControl<ListBox>("RecentList"));
    Assert.NotNull(window.GetControl<Button>("OpenFolderButton"));
    Assert.NotNull(window.GetControl<Button>("SettingsButton"));
    window.Close();
  }
}
