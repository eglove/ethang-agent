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
            // No window exists until workspace selection and bootstrap finish; the
            // transient picker/dialog host windows closing must not trip Avalonia's
            // default "shutdown when the last window closes" behavior mid-startup.
            DesktopHost.DeferShutdownDuringStartup(desktop);

            // Startup splits three ways: workspace selection first (folder dialogs are
            // UI-affine, so the decision loop runs on the UI thread), then config load +
            // SQLite session save on a background thread, then window construction back on
            // the UI thread (Avalonia controls are thread-affine). Declining to choose a
            // workspace exits cleanly with code 0; bootstrap failures surface as an error
            // dialog and a non-zero exit inside DesktopHost.
            _ = Task.Run(async () =>
            {
                var root = await Dispatcher.UIThread.InvokeAsync(
                    () => SelectWorkspaceOrShutdownAsync(desktop));
                if (root is null)
                    return; // user declined to pick a workspace; shutdown already scheduled

                try
                {
                    var boot = await DesktopHost.PrepareAsync(desktop, root);
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

    /// <summary>Runs the workspace decision loop on the UI thread. Returns the chosen root,
    ///     or null when the user chose to exit (clean shutdown scheduled here).</summary>
    private static async Task<string?> SelectWorkspaceOrShutdownAsync(
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        var flow = new WorkspaceStartupFlow();
        var result = await flow.RunAsync(
            () => DesktopHost.PickWorkspaceFolderAsync(desktop),
            () => DesktopHost.ShowRequiredDialogAsync(desktop));

        if (result.ExitRequested)
        {
            desktop.Shutdown(0);
            return null;
        }
        return result.Root;
    }
}