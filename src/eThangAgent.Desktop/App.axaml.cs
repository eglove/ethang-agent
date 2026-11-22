using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using eThangAgent.Composition;
using eThangAgent.Desktop.Markdown;
using eThangAgent.Desktop.ViewModels;
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

  // The process's window sequence: the launcher, then one workspace's main window. It owns the
  // bootstrap, so a second instance would open the app database twice.
  private WorkspaceHost? _host;

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
      // The process releases its claim on the open workspace's name as it shuts down, so the
      // next launch for that directory is not signalled into a process that is going away.
      desktop.ShutdownRequested += (_, _) => _host?.Dispose();

      // Startup is two phases: config load + key recovery + session-factory construction on a
      // background thread (no Avalonia controls), then the launcher on the UI thread. No
      // workspace is chosen at startup: the app always stops at the launcher, and no API key is
      // required until an agent runs. Infrastructure failures surface as an error dialog and a
      // non-zero exit, and the launcher is never shown after a failed bootstrap.
      _ = Task.Run(async () =>
      {
        try
        {
          DesktopBootstrap boot = await DesktopHost.PrepareAsync();
          await Dispatcher.UIThread.InvokeAsync(() =>
                {
                  WorkspaceHost host = new(desktop, boot);
                  host.SettingsRequested += async (_, _) => await ShowSettingsAsync(host).ConfigureAwait(true);
                  _host = host;
                  host.ShowLauncher();
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

  /// <summary>Shows the app Settings window from the launcher, before any workspace exists: the
  ///     prefill comes from the bootstrap and a confirmed save lands through the same
  ///     <see cref="SettingsPersistence"/> the shell's save uses.</summary>
  /// <param name="host">The host whose bootstrap the save is applied to.</param>
  private static async Task ShowSettingsAsync(WorkspaceHost host)
  {
    ArgumentNullException.ThrowIfNull(host);
    DesktopBootstrap boot = host.Bootstrap;
    if (host.Launcher is not { } owner)
    {
      return;
    }

    SettingsWindow dialog = new(DesktopHost.SettingsValuesFrom(boot));
    SettingsUpdate? update = await dialog.ShowDialog<SettingsUpdate?>(owner).ConfigureAwait(true);
    if (update is null)
    {
      return; // user cancelled — no-op
    }

    PersistedSettings persisted = await new SettingsPersistence(
        boot.Preferences, boot.ApiKeys)
        .ApplyAsync(update, boot.PreferredProviderId).ConfigureAwait(true);

    // The next workspace's agents read the credentials from the bootstrap's settings, so the save
    // is applied here as well as persisted: a credential entered with no workspace open is in
    // force the moment a workspace opens.
    AgentSettings applied = boot.Settings.WithApiKeys(persisted.OpenRouterKey)
        .WithManagementKey(persisted.ManagementKey)
        .WithCloudflareCredentialSaved(persisted.CloudflareAccountId, persisted.CloudflareApiToken,
            persisted.CloudflareGatewayId);
    host.Bootstrap = boot with
    {
      Settings = applied,
      Sessions = boot.Sessions.WithSettings(applied),
      CommitStyle = update.CommitStyle,
      CompactionHandoff = update.CompactionHandoff,
    };
  }
}
