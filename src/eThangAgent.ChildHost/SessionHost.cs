using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ModelDomain;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

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
  ///     the app-owned database path (CLI arg — one host per app, sharing its database).</summary>
  public static SessionHost Create(string settingsJsonPath, string databasePath)
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

    return new SessionHost(
        services.GetRequiredService<IAgentStore>(),
        services.GetRequiredService<SubAgentSpawner>());
  }

  private static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);

  /// <summary>Sub-agents never ask humans questions: the clarify tool surfaces a typed
  ///      refusal that the child model can act on.</summary>
  private sealed class NullClarifyChannel : IClarifyChannel
  {
    public System.Threading.Tasks.Task<eThangAgent.SharedKernel.Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default)
        => System.Threading.Tasks.Task.FromResult(
            eThangAgent.SharedKernel.Result.Failure<string>(
                new eThangAgent.SharedKernel.DomainError("ClarifyUnavailable",
                    "sub-agents cannot reach the human; answer from context or proceed without.")));
  }
}
