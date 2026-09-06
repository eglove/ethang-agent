using System.Text.Json;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ModelDomain;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.ChildHost;

/// <summary>The host's own headless session composition: the provider stack built from the
///     settings JSON the app persists for the host (API keys travel host-side once at startup,
///     never per-envelope), sharing the SAME database file so records and transcripts written
/// </summary>
public sealed class SessionHost
{
  private SessionHost(IServiceProvider services, IAgentStore store, IAgentRuntime runtime,
      ChildMailboxRegistry mailboxes, WatchdogSettings? watchdog)
  {
    Services = services;
    Store = store;
    Runtime = runtime;
    Mailboxes = mailboxes;
    Watchdog = watchdog;
  }

  /// <summary>The child container — the host watchdog resolves the same seams the app
  ///     side does (heartbeat, event store, event stream, supervisor registry) from it.</summary>
  public IServiceProvider Services { get; }
  public IAgentStore Store { get; }
  public IAgentRuntime Runtime { get; }

  /// <summary>The container's live-child mailbox registry — the delivery view the
  ///     server resolves 'deliver' envelopes through, so wire-delivered steering lands
  ///     in the SAME box the running child's loop drains (W3).</summary>
  public ChildMailboxRegistry Mailboxes { get; }

  /// <summary>The SubAgent:Watchdog configuration the app shipped (W1.2): null when no
  ///     watchdog section was configured. The settings JSON is the only carrier; the
  ///     host never reads app configuration itself.</summary>
  public WatchdogSettings? Watchdog { get; }

  /// <summary>The options the host watchdog runs under: the shipped configuration
  ///     translated strictly, or <see cref="WatchdogOptions.Default"/> when no
  ///     watchdog section was configured. The single source of truth for the server's
  ///     watchdog construction — configured values govern exactly (W1.2).</summary>
  public WatchdogOptions EffectiveWatchdogOptions => Watchdog?.ToOptions() ?? WatchdogOptions.Default;

  /// <summary>Builds from the settings JSON the app writes before launching the host, plus
  ///     the app-owned database path (CLI arg — one host per app, sharing its database).
  ///     The container's <see cref="ChildMailboxRegistry"/> is exposed as
  ///     <see cref="Mailboxes"/> so the server's 'deliver' path resolves the SAME box
  ///     the running child's loop drains (FR-C2, W3 delivery fix).</summary>
  public static SessionHost Create(string settingsJsonPath, string databasePath)
  {
    string json = File.ReadAllText(settingsJsonPath);
    AgentSettings deserialized = JsonSerializer.Deserialize<AgentSettings>(json, Options)
        ?? throw new InvalidOperationException("host settings deserialized to null.");
    // Strict boundary (W1.2 found this): STJ binds members ABSENT from the JSON to null
    // even though the record declares them required — the supervisor always serializes
    // the full settings, but a hand-written file must fail with a NAMED error here,
    // never a null-reference fault deep in composition.
    if (deserialized.OpenRouter is null || deserialized.Zai is null || deserialized.SubAgents is null)
    {
      List<string> missing = [];
      if (deserialized.OpenRouter is null)
      {
        missing.Add("OpenRouter");
      }

      if (deserialized.Zai is null)
      {
        missing.Add("Zai");
      }

      if (deserialized.SubAgents is null)
      {
        missing.Add("SubAgents");
      }
      throw new InvalidOperationException(
          $"host settings JSON is missing required member(s): {string.Join(", ", missing)}.");
    }

    // The host ALWAYS runs children in its own process via its in-process runtime.
    // The app's RemoteHost flag travels in the same settings JSON (the supervisor
    // serializes the whole AgentSettings), and honoring it here would wire the host's
    // container for ANOTHER remote hop — a runtime with no supervisor that fails every
    // start HostUnavailable (observed: children stuck Running attempts=0 forever).
    AgentSettings settings = deserialized with { RemoteHost = false };

    string providerName = settings.OpenRouter.ApiKey is not null ? Providers.OpenRouter : Providers.Zai;
    string workspace = ResolveWorkspace(settings.WorkspaceRoot, settingsJsonPath);
    ModelConfig bootstrapModel = ModelConfig.Create(
        Providers.FallbackModelId(providerName), null, 32 * 1024, 0.7f,
        Providers.RoutingContextWindow).Value!;

    ServiceProvider services = new ServiceCollection()
        .AddEThangAgentCore(
            settings, providerName, bootstrapModel,
            new AgentHostOptions(
                new FixedWorkspaceContext(workspace),
                new WorkspacePathResolver(workspace),
                [new SessionFilesPromptProvider(workspace, settings.SessionFilesGlobal, settings.SessionFilesWorkspace)]),
            new AppDatabase(databasePath),
            null)
        .BuildServiceProvider();

    return new SessionHost(
        services,
        services.GetRequiredService<IAgentStore>(),
        services.GetRequiredService<IAgentRuntime>(),
        services.GetRequiredService<ChildMailboxRegistry>(),
        settings.Watchdog);
  }

  /// <summary>The workspace the host's container anchors at (D): the root the app
  ///     ships in the settings JSON when present - absolute only, a relative root is
  ///     a named startup error (strict boundaries; never a silent resolution against
  ///     a random current directory) - else the legacy fallback: the settings JSON's
  ///     own directory, kept for settings files written before the anchor existed.</summary>
  private static string ResolveWorkspace(string? workspaceRoot, string settingsJsonPath)
  {
    return workspaceRoot switch
    {
      { } root when string.IsNullOrWhiteSpace(root) =>
          Path.GetDirectoryName(settingsJsonPath) ?? AppContext.BaseDirectory,
      { } root when Path.IsPathRooted(root) => root,
      { } root => throw new InvalidOperationException(
          "WorkspaceRoot must be an absolute path: '" + root + "'."),
      _ => Path.GetDirectoryName(settingsJsonPath) ?? AppContext.BaseDirectory,
    };
  }

  private static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);

}
