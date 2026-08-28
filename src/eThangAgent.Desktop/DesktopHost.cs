using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;

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
    IApiKeyProtector ApiKeys);

/// <summary>Composition root for the desktop frontend: shared core + desktop-specific seams.
///     Startup loads configuration (provider API keys come from the app database, DPAPI-
///     protected — never from environment variables) and shows the shell immediately;
///     each 'Open Agent' pick builds an isolated <see cref="AgentSession"/> whose directory
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

    settings = settings.WithApiKeys(
        await LoadKeyAsync(preferences, protector, OpenRouterSettings.PreferenceKey),
        await LoadKeyAsync(preferences, protector, ZaiSettings.PreferenceKey));

    return new DesktopBootstrap(
        new AgentSessionFactory(settings, database),
        settings,
        await ResolvePreferredProviderAsync(settings, preferences),
        preferences,
        protector);
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
    MainViewModel vm = new(
        createSession: null,
        preferredProviderId: boot.PreferredProviderId,
        preferences: boot.Preferences,
        settings: boot.Settings,
        sessionFactory: boot.Sessions,
        keyProtector: boot.ApiKeys);
    return new MainWindow(vm);
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
    return (command, ct, contentDelta, reasoningDelta, iterationEnd, toolCall, toolResult, notice) =>
    {
      // Suppress the execution context along with the thread switch: Task.Run alone
      // still flows the caller's SynchronizationContext (.NET 6+), which would pin
      // the domain loop's continuations to the UI pump.
      Task<Result<string>> scheduled;
      using (ExecutionContext.SuppressFlow())
      {
        scheduled = Task.Run(() => inner(command, ct, contentDelta,
                reasoningDelta, iterationEnd, toolCall, toolResult, notice));
      }

      return scheduled;
    };
  }
}
