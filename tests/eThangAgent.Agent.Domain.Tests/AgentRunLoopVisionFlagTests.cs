using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>The child run's model config carries the catalog's vision capability:
///     the spawner consults the session catalog for the child record's model the same
///     way it resolves the context window; a missing catalog keeps the legacy false.
///     The provider's ConfigsSeen is the observation seam — it sees exactly the config
///     the spawner built.</summary>
public class AgentRunLoopVisionFlagTests
{
  private static readonly DateTimeOffset FixedNow = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

  private sealed class VisionCatalog(params (string ModelId, bool Vision)[] entries) : IModelCatalog
  {
    public Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<ModelProviderEntry>>(
            [.. entries.Select(e => new ModelProviderEntry(
                e.ModelId, "TestProvider", 0.000001m, null, 0.000002m, 128_000, 8_192,
                SupportsToolUse: true, SupportsVision: e.Vision, SupportsTextOutput: true,
                null, null, null, null, null, null))]));
  }

  private static AgentRecord Child(string model = "vision/model")
      => AgentRecord.Started(AgentId.NewId(), model, null, "do things", FixedNow);

  /// <summary>A vendor-keyed resolver over one catalog, which every vendor lists: the shape a
  ///     test uses when the vendor does not matter.</summary>
  private sealed class AnyVendor(IModelCatalog catalog) : IModelProviderResolver
  {
    public Result<IModelCatalog> Catalog(string vendorId) => Result.Success(catalog);

    public Result<IModelProvider> Resolve(string vendorId, ModelConfig config) =>
        Result.Failure<IModelProvider>(new DomainError("ProviderNotConfigured", "none"));
  }

  private sealed class FixedSource : IContextWindowSource
  {
    public Task<int?> WindowForAsync(string modelId, string? vendorId = null, CancellationToken ct = default)
        => Task.FromResult<int?>(128_000);
  }

  [Fact]
  public async Task RunAsync_VisionCapableChildModel_ConfigCarriesFlagTrue()
  {
    FakeProvider provider = new(Result.Success(new ModelResponse("report", [])));
    FakeModelProviderFactory factory = new(provider);
    AgentRunLoop spawner = new(new AgentRunServices(factory, new FakeAgentStore(),
            new ToolRegistry([]), new StaticPromptProvider("guide"),
            new AgentRunOptions(DefaultModel: "m")),
        windowSource: new FixedSource(),
        resolver: new AnyVendor(new VisionCatalog(("vision/model", true))));

    _ = await spawner.RunAsync(Child("vision/model"), CancellationToken.None);

    Assert.True(factory.LastConfig!.AcceptsImageInput);
  }

  [Fact]
  public async Task RunAsync_TextOnlyChildModel_ConfigCarriesFlagFalse()
  {
    FakeProvider provider = new(Result.Success(new ModelResponse("report", [])));
    FakeModelProviderFactory factory = new(provider);
    AgentRunLoop spawner = new(new AgentRunServices(factory, new FakeAgentStore(),
            new ToolRegistry([]), new StaticPromptProvider("guide"),
            new AgentRunOptions(DefaultModel: "m")),
        windowSource: new FixedSource(),
        resolver: new AnyVendor(new VisionCatalog(("other/model", true))));

    _ = await spawner.RunAsync(Child("vision/model"), CancellationToken.None);

    Assert.False(factory.LastConfig!.AcceptsImageInput);
  }

  [Fact]
  public async Task RunAsync_NoCatalog_ConfigDefaultsFalse()
  {
    FakeProvider provider = new(Result.Success(new ModelResponse("report", [])));
    FakeModelProviderFactory factory = new(provider);
    AgentRunLoop spawner = new(new AgentRunServices(factory, new FakeAgentStore(),
            new ToolRegistry([]), new StaticPromptProvider("guide"),
            new AgentRunOptions(DefaultModel: "m")),
        windowSource: new FixedSource());

    _ = await spawner.RunAsync(Child("vision/model"), CancellationToken.None);

    Assert.False(factory.LastConfig!.AcceptsImageInput);
  }
}
