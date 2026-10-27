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
    public Task<int?> WindowForAsync(string modelId, CancellationToken ct = default)
        => Task.FromResult<int?>(128_000);
  }

  private static SubAgentSpawner Spawner(FakeModelProviderFactory factory, SessionModelPreferences preferences)
      => new(new SubAgentServices(factory, new FakeAgentStore(), new ToolRegistry([]),
              new StaticPromptProvider("guide"), new SubAgentOptions(DefaultModel: "m")),
          preferences, windowSource: new FixedSource());

  [Fact]
  public async Task RunAsync_SessionVariantCommitted_ChildConfigCarriesItOnItsOwnModel()
  {
    // THE VARIANT IS A PROPERTY OF THE CONVERSATION, not of the root agent: a child must route
    // the same way its parent does, or the session's cost and cache ratio describe a mixture
    // nobody chose. It is applied to the CHILD'S OWN model - the overlay writes the suffix onto
    // whatever model the child config names, which is why the assertion names 'alpha/model'
    // rather than the session's model.
    FakeModelProviderFactory factory = new(new FakeProvider(Result.Success(new ModelResponse("report", []))));
    SubAgentSpawner spawner = Spawner(factory, new SessionModelPreferences
    {
      ModelId = "alpha/model",
      ModelVariant = ModelVariant.Nitro,
    });

    _ = await spawner.RunAsync(Child(), CancellationToken.None);

    Assert.Equal("alpha/model:nitro", factory.LastConfig!.ModelId);
  }

  [Fact]
  public async Task RunAsync_NoVariantCommitted_ChildConfigKeepsTheBareModel()
  {
    // Null means UNSET, never 'standard was chosen': a session that committed no variant keeps
    // the provider's own routing, exactly as every pre-existing session does.
    FakeModelProviderFactory factory = new(new FakeProvider(Result.Success(new ModelResponse("report", []))));
    SubAgentSpawner spawner = Spawner(factory, new SessionModelPreferences { ModelId = "alpha/model" });

    _ = await spawner.RunAsync(Child(), CancellationToken.None);

    Assert.Equal("alpha/model", factory.LastConfig!.ModelId);
  }

  [Fact]
  public async Task RunAsync_TheChildWindowLookup_IsKeyedOnTheBaseModelNotTheWireId()
  {
    // A catalog knows 'alpha/model' while the wire carries 'alpha/model:nitro'. Looking up the
    // suffixed id would find no window and fail every child of a nitro session - the regression
    // this pins, since the failure would read as a broken model rather than a broken key.
    FakeModelProviderFactory factory = new(new FakeProvider(Result.Success(new ModelResponse("report", []))));
    SubAgentSpawner spawner = Spawner(factory, new SessionModelPreferences
    {
      ModelId = "alpha/model",
      ModelVariant = ModelVariant.Floor,
    });

    _ = await spawner.RunAsync(Child("alpha/model:floor"), CancellationToken.None);

    Assert.Equal("alpha/model:floor", factory.LastConfig!.ModelId);
  }
}
