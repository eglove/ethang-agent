using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // No window exists until bootstrap finishes; transient helper windows closing
            // must not trip Avalonia's default "shutdown when the last window closes"
            // behavior mid-startup.
            DesktopHost.DeferShutdownDuringStartup(desktop);

            // Startup is now two phases: config load + session-factory construction on a
            // background thread (no Avalonia controls), then shell-window construction on
            // the UI thread. No workspace is requested at startup — agents open per tab
            // via 'Open Agent'. Bootstrap failures surface as an error dialog and a
            // non-zero exit inside DesktopHost.
            _ = Task.Run(async () =>
            {
                try
                {
                    var boot = await DesktopHost.PrepareAsync(desktop);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        var window = DesktopHost.CreateMainWindow(desktop, boot);
                        desktop.MainWindow = window;
                        window.Show();
                        // A real window now owns the lifetime: closing it should exit.
                        DesktopHost.EnableWindowCloseShutdown(desktop);
                    });
                }
                catch (UnreachableException)
                {
                    // Error dialog path already scheduled shutdown(1) inside DesktopHost.
                }
                catch (Exception ex)
                {
                    // Never exit silently: surface ANY bootstrap failure in a visible dialog.
                    Console.Error.WriteLine(ex);
                    await DesktopHost.ShowErrorAndExitAsync(desktop,
                        "eThang Agent failed to start: " + ex.Message);
                }
            });
        }
        base.OnFrameworkInitializationCompleted();
    }
}
