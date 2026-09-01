using System.Text.Json;
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
  private SessionHost(IAgentStore store, SubAgentSpawner spawner)
  {
    Store = store;
    Spawner = spawner;
  }

  public IAgentStore Store { get; }
  public SubAgentSpawner Spawner { get; }

  /// <summary>Builds from the settings JSON the app writes before launching the host, plus
  ///     the app-owned database path (CLI arg — one host per app, sharing its database).
  ///     <paramref name="inboxFor"/> hands each child its steering mailbox (FR-C2): the
  ///     server owns the registry so 'deliver' envelopes reach the running child.</summary>
  public static SessionHost Create(string settingsJsonPath, string databasePath, Func<AgentId, IAgentInbox?>? inboxFor = null)
  {
    string json = File.ReadAllText(settingsJsonPath);
    AgentSettings settings = JsonSerializer.Deserialize<AgentSettings>(json, Options)
        ?? throw new InvalidOperationException("host settings deserialized to null.");

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
          services.GetRequiredService<IAgentStore>(),
          new SubAgentSpawner(childServices));
    }

    return new SessionHost(
        services.GetRequiredService<IAgentStore>(),
        services.GetRequiredService<SubAgentSpawner>());
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
