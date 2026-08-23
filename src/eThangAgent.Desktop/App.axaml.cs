using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Themes.Fluent;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Bootstrap off the UI thread: config load + SQLite session save do real I/O.
            // The window is shown on the UI thread once the host is ready; startup errors
            // surface as an error dialog and a non-zero shutdown inside DesktopHost.
            _ = Task.Run(async () =>
            {
                try
                {
                    var window = await DesktopHost.CreateMainWindowAsync(desktop);
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        desktop.MainWindow = window;
                        window.Show();
                    });
                }
                catch (UnreachableException)
                {
                    // Error dialog path already scheduled shutdown(1) inside DesktopHost.
                }
                catch (Exception ex)
                {
                    // Never exit silently: surface ANY bootstrap failure (bad config,
                    // locked database, missing key) in a visible dialog, then exit non-zero.
                    Console.Error.WriteLine(ex);
                    await DesktopHost.ShowErrorAndExitAsync(desktop,
                        "eThang Agent failed to start: " + ex.Message);
                }
            });
        }
        base.OnFrameworkInitializationCompleted();
    }
}