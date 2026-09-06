using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using eThangAgent.Desktop.Markdown;
using eThangAgent.Desktop.Views;
using Markdig;
using MarkView.Avalonia;
using TextMateSharp.Grammars;

namespace eThangAgent.Desktop;

internal class App : Application
{
#pragma warning disable S1144, CA1823, IDE0052 // by-design: the initializer IS the registration
  // App-lifetime subscription, registered once at type init and never disposed
  // by design: this handler IS the single link-routing gate for every
  // MarkdownViewer the app ever creates (transcript blocks, future surfaces).
  private static readonly IDisposable LinkHandlerSubscription =
      MarkdownViewer.LinkClickedEvent.AddClassHandler<MarkdownViewer>((_, e) => MarkdownLinkLauncher.TryOpen(e.Url));
#pragma warning restore S1144, CA1823, IDE0052

  public override void Initialize() => AvaloniaXamlLoader.Load(this);

  public override void OnFrameworkInitializationCompleted()
  {
    // Transcript markdown rendering is MarkView-backed: defaults are set once so
    // every MarkdownViewer (transcript blocks and future surfaces) inherits the
    // same pipeline and highlighting. Registered outside the desktop-lifetime
    // gate so the headless test app exercises the identical wiring.
    MarkdownViewerDefaults.Pipeline = new MarkdownPipelineBuilder()
        .UseSupportedExtensions()
        .Build();
    MarkdownViewerDefaults.Extensions.AddTextMateHighlighting(ThemeName.DarkPlus, ThemeName.LightPlus);

    if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
    {
      // No window exists until bootstrap finishes; transient helper windows closing
      // must not trip Avalonia's default "shutdown when the last window closes"
      // behavior mid-startup.
      DesktopHost.DeferShutdownDuringStartup(desktop);

      // Startup is now two phases: config load + key recovery + session-factory
      // construction on a background thread (no Avalonia controls), then shell-window
      // construction on the UI thread. No workspace is requested at startup — agents
      // open per tab via 'Open Workspace', and no API key is required until then.
      // Infrastructure failures surface as an error dialog and a non-zero exit
      // inside DesktopHost.
      _ = Task.Run(async () =>
      {
        try
        {
          DesktopBootstrap boot = await DesktopHost.PrepareAsync();
          await Dispatcher.UIThread.InvokeAsync(() =>
                {
                  MainWindow window = DesktopHost.CreateMainWindow(boot);
                  desktop.MainWindow = window;
                  window.Show();
                  // A real window now owns the lifetime: closing it should exit.
                  DesktopHost.EnableWindowCloseShutdown(desktop);
                });
        }
        // Named decision (CA1031): startup is a fault boundary — ANY failure must
        // surface in the error dialog, never kill the process silently.
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
        {
          // Never exit silently: surface ANY bootstrap failure in a visible dialog.
          await Console.Error.WriteLineAsync(ex.ToString());
          await DesktopHost.ShowErrorAndExitAsync(desktop,
                    "eThang Agent failed to start: " + ex.Message);
        }
#pragma warning restore CA1031
      });
    }
    base.OnFrameworkInitializationCompleted();
  }
}
