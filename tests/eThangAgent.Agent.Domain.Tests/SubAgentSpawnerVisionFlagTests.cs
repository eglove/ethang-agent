using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>The child run's model config carries the catalog's vision capability:
///     the spawner consults the session catalog for the child record's model the same
///     way it resolves the context window; a missing catalog keeps the legacy false.
///     The provider's ConfigsSeen is the observation seam — it sees exactly the config
///     the spawner built.</summary>
public class SubAgentSpawnerVisionFlagTests
{
  private static readonly DateTimeOffset FixedNow = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

  private sealed class VisionCatalog(params (string ModelId, bool Vision)[] entries) : IModelCatalog
  {
    public Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<ModelProviderEntry>>(
            [.. entries.Select(e => new ModelProviderEntry(
                e.ModelId, "TestProvider", 0.000001m, 0.000002m, 128_000, 8_192,
                SupportsToolUse: true, SupportsVision: e.Vision,
                null, null, null, null, null, null))]));
  }

  private static AgentRecord Child(string model = "vision/model")
      => AgentRecord.Spawned(AgentId.NewId(), null, 0, model, null, "do things", FixedNow);

  private sealed class FixedSource : IContextWindowSource
  {
    public Task<int?> WindowForAsync(string modelId, string? providerName, CancellationToken ct = default)
        => Task.FromResult<int?>(128_000);
  }

  [Fact]
  public async Task RunAsync_VisionCapableChildModel_ConfigCarriesFlagTrue()
  {
    FakeProvider provider = new(Result.Success(new ModelResponse("report", [])));
    FakeModelProviderFactory factory = new(provider);
    SubAgentSpawner spawner = new(new SubAgentServices(factory, new FakeAgentStore(),
            new ToolRegistry([]), new StaticPromptProvider("guide"),
            new SubAgentOptions(DefaultModel: "m")),
        windowSource: new FixedSource(), catalog: new VisionCatalog(("vision/model", true)));

    _ = await spawner.RunAsync(Child("vision/model"), CancellationToken.None);

    Assert.True(factory.LastConfig!.AcceptsImageInput);
  }

  [Fact]
  public async Task RunAsync_TextOnlyChildModel_ConfigCarriesFlagFalse()
  {
    FakeProvider provider = new(Result.Success(new ModelResponse("report", [])));
    FakeModelProviderFactory factory = new(provider);
    SubAgentSpawner spawner = new(new SubAgentServices(factory, new FakeAgentStore(),
            new ToolRegistry([]), new StaticPromptProvider("guide"),
            new SubAgentOptions(DefaultModel: "m")),
        windowSource: new FixedSource(), catalog: new VisionCatalog(("other/model", true)));

    _ = await spawner.RunAsync(Child("vision/model"), CancellationToken.None);

    Assert.False(factory.LastConfig!.AcceptsImageInput);
  }

  [Fact]
  public async Task RunAsync_NoCatalog_ConfigDefaultsFalse()
  {
    FakeProvider provider = new(Result.Success(new ModelResponse("report", [])));
    FakeModelProviderFactory factory = new(provider);
    SubAgentSpawner spawner = new(new SubAgentServices(factory, new FakeAgentStore(),
            new ToolRegistry([]), new StaticPromptProvider("guide"),
            new SubAgentOptions(DefaultModel: "m")),
        windowSource: new FixedSource());

    _ = await spawner.RunAsync(Child("vision/model"), CancellationToken.None);

    Assert.False(factory.LastConfig!.AcceptsImageInput);
  }
}
