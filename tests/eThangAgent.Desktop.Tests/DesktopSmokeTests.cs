using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using eThangAgent.Desktop;
using eThangAgent.Desktop.Views;

[assembly: AvaloniaTestApplication(typeof(eThangAgent.Desktop.Tests.TestApp))]

namespace eThangAgent.Desktop.Tests;

public class TestApp
{
    internal static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public class DesktopSmokeTests
{
    [AvaloniaFact]
    public void MainWindow_Instantiates_And_Has_Title()
    {
        var window = new MainWindow();
        Assert.Equal("eThang Agent", window.Title);
    }
}
