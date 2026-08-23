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
            // Bootstrap splits by thread affinity: config load + SQLite session save run on a
            // background thread; window construction MUST happen on the UI thread (Avalonia
            // controls are thread-affine). Startup failures surface as an error dialog and a
            // non-zero exit inside DesktopHost.
            _ = Task.Run(async () =>
            {
                try
                {
                    var boot = await DesktopHost.PrepareAsync(desktop);
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        var window = DesktopHost.CreateMainWindow(desktop, boot);
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