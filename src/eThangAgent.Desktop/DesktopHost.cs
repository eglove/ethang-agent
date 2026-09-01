using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using eThangAgent.Agent.Application;
using eThangAgent.Agent.Application.Sessions;
using eThangAgent.AgentDomain;
using eThangAgent.AgentInfrastructure;
using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.Zai.ACL;
using Microsoft.Extensions.DependencyInjection;
using CommitStyle = eThangAgent.ToolDomain.CommitStyle;
using CommitStylePreference = eThangAgent.ToolDomain.CommitStylePreference;

namespace eThangAgent.Desktop;

/// <summary>Everything <see cref="DesktopHost.CreateMainWindow"/> needs, prepared OFF the UI thread:
///     validated configuration with the API keys lifted from app preferences, the
///     provider-aware session factory the shell uses to open one isolated agent
///     container per chosen directory, the settings snapshot behind the Settings modal,
///     the preferred provider, the preference store, and the key protector. No agent
///     exists until the user opens one; no key is required until then either.</summary>
internal sealed record DesktopBootstrap(
    AgentSessionFactory Sessions,
    AgentSettings Settings,
    string PreferredProviderId,
    IAppPreferenceStore Preferences,
    IApiKeyProtector ApiKeys,
    SessionCatalogQueryHandler Catalog,
    CommitStyle CommitStyle);

/// <summary>Composition root for the desktop frontend: shared core + desktop-specific seams.
///     Startup loads configuration (provider API keys come from the app database, DPAPI-
///     protected — never from environment variables) and shows the shell immediately;
///     each 'Open Workspace' pick builds an isolated <see cref="AgentSession"/> whose directory
///     roots path resolution, workspace identity, and — when an AGENTS.md exists there — a
///     verbatim system-prompt injection announcing it as read. Startup infrastructure
///     failures surface as an error dialog followed by exit code 1; a missing key is NOT
///     one — the unkeyed provider simply is not offered.</summary>
internal static class DesktopHost
{
  /// <summary>Background-thread-safe preparation: strict config load, key recovery from
  ///     app preferences, and the provider-aware session factory. Constructs NO Avalonia
  ///     controls (they are thread-affine and must be built on the UI thread via
  ///     <see cref="CreateMainWindow"/>). No workspace is required up front: agents are
  ///     opened per tab from the shell.</summary>
  public static async Task<DesktopBootstrap> PrepareAsync()
  {
    AgentSettings settings = AgentConfiguration.Load();

    // ONE app-owned database for every opened session (rows are keyed by workspace id).
    AppDatabase database = new();
    IAppPreferenceStore preferences = new SqliteAppPreferenceStore(database);
    IApiKeyProtector protector = new DpapiKeyProtector();

    settings = settings
        .WithApiKeys(
            await LoadKeyAsync(preferences, protector, OpenRouterSettings.PreferenceKey),
            await LoadKeyAsync(preferences, protector, ZaiSettings.PreferenceKey))
        .WithZaiEndpointMode(await LoadEndpointModeAsync(preferences));
    CommitStyle commitStyle = await LoadCommitStyleAsync(preferences);

    // The Sessions dialog reads the shared store directly — it must work with zero
    // tabs open, i.e. outside any per-session container.
    SessionCatalogQueryHandler catalog = new(new SqliteAgentStore(database));

    return new DesktopBootstrap(
        new AgentSessionFactory(settings, database),
        settings,
        await ResolvePreferredProviderAsync(settings, preferences),
        preferences,
        protector,
        catalog,
        commitStyle);
  }

  /// <summary>Recovers one stored key: absent stays null; undecryptable (corrupted or
  ///     foreign blob) reads as absent with a stderr note, never a crash.</summary>
  private static async Task<string?> LoadKeyAsync(
      IAppPreferenceStore preferences, IApiKeyProtector protector, string preferenceKey)
  {
    string? stored = await preferences.GetAsync(preferenceKey);
    if (stored is null)
    {
      return null;
    }

    string? key = protector.Unprotect(stored);
    if (key is null)
    {
      await Console.Error.WriteLineAsync($"stored '{preferenceKey}' could not be decrypted; treating as unconfigured");
    }
    return key;
  }

  /// <summary>Recovers the stored z.ai endpoint mode: absent stays at the CodingPlan
  ///     default; a stored value that no longer parses (corrupted or foreign row) reads
  ///     as absent with a stderr note, never a crash.</summary>
  private static async Task<ZaiEndpointMode> LoadEndpointModeAsync(IAppPreferenceStore preferences)
  {
    string? stored = await preferences.GetAsync(ZaiSettings.EndpointModePreferenceKey);
    if (stored is null)
    {
      return ZaiEndpointMode.CodingPlan;
    }

    if (stored.TryParseConfigValue(out ZaiEndpointMode mode))
    {
      return mode;
    }

    await Console.Error.WriteLineAsync(
        $"stored '{ZaiSettings.EndpointModePreferenceKey}' value '{stored}' is not a valid endpoint mode; using the coding-plan default");
    return ZaiEndpointMode.CodingPlan;
  }
  /// <summary>Recovers the stored commit style: absent stays at the Conventional
  ///     default; an unrecognized stored value logs to stderr and falls back to the
  ///     default for the PREFILL only — the tool side surfaces the typed error if a
  ///     commit actually runs against corrupt data.</summary>
  private static async Task<CommitStyle> LoadCommitStyleAsync(IAppPreferenceStore preferences)
  {
    string? stored = await preferences.GetAsync(AppPreferenceCommitStyleProvider.PreferenceKey);
    Result<CommitStyle> resolved = CommitStylePreference.Resolve(stored);
    if (!resolved.IsSuccess)
    {
      await Console.Error.WriteLineAsync(
          $"stored '{AppPreferenceCommitStyleProvider.PreferenceKey}' value '{stored}' is not a valid commit style; using the conventional default");
      return CommitStyle.Conventional;
    }

    return resolved.Value;
  }

  /// <summary>The provider the new-agent dialog pre-selects: the persisted choice when it
  ///     is still configured, else the only configured provider, else OpenRouter (the
  ///     both-keys back-compat default).</summary>
  private static async Task<string> ResolvePreferredProviderAsync(
      AgentSettings settings, IAppPreferenceStore preferences)
  {
    string? persisted = await preferences.GetAsync(Providers.PreferenceKey).ConfigureAwait(false);
    if (persisted == Providers.OpenRouter && settings.HasOpenRouter)
    {
      return Providers.OpenRouter;
    }

    if (persisted == Providers.Zai && settings.HasZai)
    {
      return Providers.Zai;
    }

    bool preferOpenRouter = settings.HasOpenRouter;
    return preferOpenRouter ? Providers.OpenRouter : Providers.Zai;
  }

  /// <summary>Defers shutdown while startup runs. Between framework initialization and
  ///     the main window being shown, NO window exists yet except transient helpers (the
  ///     folder-picker host); under Avalonia's default OnLastWindowClose mode, closing
  ///     one of those with nothing else open shuts the app down mid-startup. While
  ///     deferred, only explicit <c>desktop.Shutdown(...)</c> calls end the app.</summary>
  public static void DeferShutdownDuringStartup(
      IClassicDesktopStyleApplicationLifetime desktop)
  {
    ArgumentNullException.ThrowIfNull(desktop);
    desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
  }

  /// <summary>Restores window-close-driven shutdown once a real main window exists.
  /// Call on the UI thread immediately after the main window is shown.</summary>
  public static void EnableWindowCloseShutdown(
      IClassicDesktopStyleApplicationLifetime desktop)
  {
    ArgumentNullException.ThrowIfNull(desktop);
    desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
  }

  /// <summary>Builds the shell view-model and main window. MUST run on the UI thread —
  ///     Avalonia controls are thread-affine (calling this off-thread throws "Call from
  ///     invalid thread"). No agent exists yet: the window opens on the empty shell and
  ///     tabs appear as the user opens directories.</summary>
  public static MainWindow CreateMainWindow(DesktopBootstrap boot)
  {
    Dispatcher.UIThread.VerifyAccess();

    // One fresh clarify channel per opened session: each agent tab presents its own
    // pending questions through its own view-model (wired in MainViewModel when the
    // session VM is created). The presenter starts unavailable — a structured,
    // model-actionable failure — until that tab's VM installs it. No session
    // delegate is injected: the shell derives it from the factory so saved keys
    // rebind future opens.
    WatchdogOptions watchdogOptions = WatchdogOptions.Default;
    WatchdogLoop watchdogLoop = new(watchdogOptions.TickInterval, TimeProvider.System);
    ProcessMetrics metrics = new();
    WatchdogPolicy policy = WatchdogPolicyFactory.FromOptions(watchdogOptions);

    WatchdogServices ServicesFor(AgentSession session) => new(
        session.Services.GetRequiredService<IAgentStore>(),
        session.ChildRuntime,
        session.Services.GetRequiredService<IAgentHeartbeat>(),
        session.Services.GetRequiredService<IWatchdogEventStore>(),
        policy, metrics, watchdogOptions, TimeProvider.System,
        session.Services.GetService<IAgentEvents>(),
        session.Services.GetService<ChildSupervisorRegistry>());

    MainViewModel vm = new(
        createSession: null,
        new MainViewModelOptions
        {
          PreferredProviderId = boot.PreferredProviderId,
          CommitStyle = boot.CommitStyle,
          Preferences = boot.Preferences,
          Settings = boot.Settings,
          SessionFactory = boot.Sessions,
          ApiKeyProtector = boot.ApiKeys,
          SessionCatalog = boot.Catalog,
          SessionOpened = session => watchdogLoop.Attach(session.RootId,
              new AgentWatchdog(session.RootId, ServicesFor(session))),
          SessionClosed = rootId => watchdogLoop.Detach(rootId),
        });
    MainWindow window = new(vm);

    // Named decision (CA2000): the source's lifetime is the window's lifetime - disposal
    // races a cancel arriving on the same close event; RunAsync observes the token only
    // before the window can close, so a late touch cannot occur.
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
    CancellationTokenSource watchdogCts = new();
#pragma warning restore CA2000 // Call IDisposable.Dispose on object created by
    _ = Task.Run(() => watchdogLoop.RunAsync(watchdogCts.Token))
        .ContinueWith(
            static t => _ = Console.Error.WriteLineAsync("watchdog loop faulted: " + t.Exception),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    window.Closed += (_, _) =>
    {
      watchdogCts.Cancel();
      watchdogCts.Dispose();
    };
    return window;
  }

  /// <summary>Shows the startup-error dialog on the UI thread and shuts down with exit code 1
  ///     when it closes. Safe to call from any thread.</summary>
  public static async Task ShowErrorAndExitAsync(
      IClassicDesktopStyleApplicationLifetime desktop, string message)
  {
    await Dispatcher.UIThread.InvokeAsync(() =>
    {
      Button exit = new() { Content = "Exit" };
      Window dialog = new()
      {
        Title = "eThang Agent — startup error",
        SizeToContent = SizeToContent.WidthAndHeight,
        CanResize = false,
        Content = new StackPanel
        {
          Margin = new Avalonia.Thickness(24),
          Spacing = 16,
          Children =
                {
                        new TextBlock
                        {
                            Text = message,
                            TextWrapping = TextWrapping.Wrap,
                            MaxWidth = 480,
                        },
                        exit,
                },
        },
      };
      exit.Click += (_, _) => dialog.Close();
      dialog.Closed += (_, _) => desktop.Shutdown(1); // non-zero exit per spec
      dialog.Show();
    });
  }

  /// <summary>Wraps a turn runner so each turn executes on the worker pool. The agent
  /// loop must never run on the UI thread: its awaits would post back to Avalonia's
  /// SynchronizationContext, and one sync-blocking tool or script would deadlock the
  /// app (observed in production as a frozen turn with nothing persisted). UI updates
  /// flow back only through the stream sink and clarify channel, which marshal
  /// explicitly onto the dispatcher.</summary>
  public static TurnRunner OffUiThread(TurnRunner inner)
  {
    return (command, ct, callbacks, notice) =>
    {
      // Suppress the execution context along with the thread switch: Task.Run alone
      // still flows the caller's SynchronizationContext (.NET 6+), which would pin
      // the domain loop's continuations to the UI pump.
      Task<Result<string>> scheduled;
      using (ExecutionContext.SuppressFlow())
      {
        scheduled = Task.Run(() => inner(command, ct, callbacks, notice));
      }

      return scheduled;
    };
  }
}
