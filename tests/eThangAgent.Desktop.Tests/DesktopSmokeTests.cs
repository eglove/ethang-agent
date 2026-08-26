using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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

public class DesktopSmokeTests
{
  [AvaloniaFact]
  public void MainWindow_Instantiates_And_Has_Title()
  {
    MainWindow window = new();
    Assert.Equal("eThang Agent", window.Title);
  }
}
