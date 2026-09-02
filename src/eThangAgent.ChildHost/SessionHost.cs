using System.Text.Json;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.ChildHost;

/// <summary>The host's own headless session composition: the provider stack built from the
///     settings JSON the app persists for the host (API keys travel host-side once at startup,
///     never per-envelope), sharing the SAME database file so records and transcripts written
///     here are visible to the app and vice versa. Children spawned in the host get a Null
///     clarify channel — human-facing tools never reach sub-agents by contract.</summary>
public sealed class SessionHost
{
  private SessionHost(IServiceProvider services, IAgentStore store, SubAgentSpawner spawner,
      IAgentRuntime runtime, WatchdogSettings? watchdog)
  {
    Services = services;
    Store = store;
    Spawner = spawner;
    Runtime = runtime;
    Watchdog = watchdog;
  }

  /// <summary>The child container — the host watchdog resolves the same seams the app
  ///     side does (heartbeat, event store, event stream, supervisor registry) from it.</summary>
  public IServiceProvider Services { get; }
  public IAgentStore Store { get; }
  public SubAgentSpawner Spawner { get; }
  public IAgentRuntime Runtime { get; }

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
  ///     <paramref name="inboxFor"/> hands each child its steering mailbox (FR-C2): the
  ///     server owns the registry so 'deliver' envelopes reach the running child.</summary>
  public static SessionHost Create(string settingsJsonPath, string databasePath, Func<AgentId, IAgentInbox?>? inboxFor = null)
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
    string workspace = Path.GetDirectoryName(settingsJsonPath) ?? AppContext.BaseDirectory;
    ModelConfig bootstrapModel = ModelConfig.Create(
        Providers.FallbackModelId(providerName), null, 32 * 1024, 0.7f,
        Providers.RoutingContextWindow).Value!;

    ServiceProvider services = new ServiceCollection()
        .AddEThangAgentCore(
            settings, providerName, bootstrapModel,
            new AgentHostOptions(
                new NullClarifyChannel(),
                new FixedWorkspaceContext(workspace),
                new WorkspacePathResolver(workspace)),
            new AppDatabase(databasePath),
            null)
        .BuildServiceProvider();

    if (inboxFor is not null)
    {
      SubAgentServices childServices = services.GetRequiredService<SubAgentServices>() with
      {
        InboxFor = inboxFor,
      };

      return new SessionHost(
          services,
          services.GetRequiredService<IAgentStore>(),
          new SubAgentSpawner(childServices),
          services.GetRequiredService<IAgentRuntime>(),
          settings.Watchdog);
    }

    return new SessionHost(
        services,
        services.GetRequiredService<IAgentStore>(),
        services.GetRequiredService<SubAgentSpawner>(),
        services.GetRequiredService<IAgentRuntime>(),
        settings.Watchdog);
  }

  private static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);

  /// <summary>Sub-agents never ask humans questions: the clarify tool surfaces a typed
  ///      refusal that the child model can act on.</summary>
  private sealed class NullClarifyChannel : IClarifyChannel
  {
    public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default)
        => Task.FromResult(
            Result.Failure<string>(
                new DomainError("ClarifyUnavailable",
                    "sub-agents cannot reach the human; answer from context or proceed without.")));
  }
}
