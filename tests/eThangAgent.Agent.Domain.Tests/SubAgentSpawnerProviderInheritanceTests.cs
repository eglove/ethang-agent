using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>A child inherits the session's committed provider (issue #297, D4/B4).
///     <para>WHY THIS IS PINNED AT THE RUN, NOT AT THE SPAWN: the child's ModelConfig is built
///     by <c>SubAgentSpawner.ChildConfigAsync</c> from the session preferences, so the provider
///     that actually rides the child's request is decided here. The spawn command's resolved
///     config only contributes the persisted model id — a test asserting the provider at the
///     spawn would pass while every child request went out unpinned.</para>
///     <para>The observation seam is the fake factory's LastConfig: it sees exactly the config
///     the child loop sends.</para></summary>
public class SubAgentSpawnerProviderInheritanceTests
{
  private static readonly DateTimeOffset FixedNow = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

  private static AgentRecord Child(string model = "alpha/model")
      => AgentRecord.Spawned(AgentId.NewId(), null, 0, model, null, "do things", FixedNow);

  private sealed class FixedSource : IContextWindowSource
  {
    public Task<int?> WindowForAsync(string modelId, string? providerName, CancellationToken ct = default)
        => Task.FromResult<int?>(128_000);
  }

  private static SubAgentSpawner Spawner(FakeModelProviderFactory factory, SessionModelPreferences preferences)
      => new(new SubAgentServices(factory, new FakeAgentStore(), new ToolRegistry([]),
              new StaticPromptProvider("guide"), new SubAgentOptions(DefaultModel: "m")),
          preferences, windowSource: new FixedSource());

  [Fact]
  public async Task RunAsync_SessionProviderCommitted_ChildConfigCarriesIt()
  {
    FakeModelProviderFactory factory = new(new FakeProvider(Result.Success(new ModelResponse("report", []))));
    SubAgentSpawner spawner = Spawner(factory, new SessionModelPreferences
    {
      ModelId = "alpha/model",
      ProviderName = "DeepInfra",
    });

    _ = await spawner.RunAsync(Child(), CancellationToken.None);

    Assert.Equal("DeepInfra", factory.LastConfig!.Provider);
  }

  [Fact]
  public async Task RunAsync_NoProviderCommitted_ChildConfigStaysUnpinned()
  {
    // Null means UNSET, never 'no provider exists': a session that committed no provider keeps
    // the provider's own routing, exactly as every pre-existing session does.
    FakeModelProviderFactory factory = new(new FakeProvider(Result.Success(new ModelResponse("report", []))));
    SubAgentSpawner spawner = Spawner(factory, new SessionModelPreferences { ModelId = "alpha/model" });

    _ = await spawner.RunAsync(Child(), CancellationToken.None);

    Assert.Null(factory.LastConfig!.Provider);
  }
}
