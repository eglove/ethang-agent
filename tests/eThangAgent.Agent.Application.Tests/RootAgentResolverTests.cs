using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

public class RootAgentResolverTests
{
  private const string FallbackModel = "openrouter/auto";

  private sealed class FakeModelSelector(ModelSelectionResult result) : IModelSelector
  {
    public int Calls { get; private set; }

    public Task<Result<ModelSelectionResult>> SelectAsync(SmartSelectionOptions options, bool requireVision, CancellationToken ct = default)
    {
      Calls++;
      return Task.FromResult(Result.Success(result));
    }
  }

  private sealed class FailingModelSelector : IModelSelector
  {
    public Task<Result<ModelSelectionResult>> SelectAsync(SmartSelectionOptions options, bool requireVision, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<ModelSelectionResult>(new DomainError("SelectionFailed", "boom")));
  }

  private static ModelSelectionResult Selection(string modelId, string providerName = "TestProvider") => new(modelId, providerName, "reason");

  private static async Task<AgentId> SeedRootAsync(FakeAgentStore store)
  {
    AgentId rootId = AgentId.NewId();
    _ = await store.SaveAsync(AgentRecord.Root(rootId, DateTimeOffset.UtcNow, "C:/workspaces/demo", "openrouter")).ConfigureAwait(false);
    return rootId;
  }

  private static RootSessionIdentity Identity(AgentId rootId) => new() { Id = rootId };

  private static RootModelContext Ctx(FakeAgentStore? store, AgentId? rootId) =>
      new(store, rootId is null ? null : Identity(rootId.Value), FallbackModel, 2048, 0.7f, new FixedWindowSource());

  [Fact]
  public async Task NoSelector_Uses_Fallback_Without_Selection()
  {
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    RootAgentResolver resolver = new(Ctx(store, rootId), selector: null);

    (ModelConfig config, string? notice) = await resolver.ResolveAsync(requireVision: false, ct: TestContext.Current.CancellationToken);

    Assert.Equal(FallbackModel, config.ModelId);
    Assert.Null(notice);
  }

  [Fact]
  public async Task UnpinnedSession_RunsSelection_PersistsPin()
  {
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    FakeModelSelector selector = new(Selection("anthropic/claude-3.5-sonnet"));
    RootAgentResolver resolver = new(Ctx(store, rootId), selector);

    (ModelConfig config, string? notice) = await resolver.ResolveAsync(requireVision: false, ct: TestContext.Current.CancellationToken);

    Assert.Equal("anthropic/claude-3.5-sonnet", config.ModelId);
    Assert.NotNull(notice);
    Assert.Contains("Model selected: anthropic/claude-3.5-sonnet", notice, StringComparison.Ordinal);
    Assert.Equal(1, selector.Calls);
    AgentRecord updated = Assert.Single(store.Updated);
    Assert.Equal("anthropic/claude-3.5-sonnet", updated.ModelUsed);
  }

  [Fact]
  public async Task PinnedSession_ServesPin_NeverReSelects()
  {
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    FakeModelSelector selector = new(Selection("anthropic/claude-3.5-sonnet"));
    RootAgentResolver resolver = new(Ctx(store, rootId), selector);
    Result<AgentRecord> root = await store.GetAsync(rootId, TestContext.Current.CancellationToken);
    Assert.True(root.IsSuccess);
    AgentRecord pinned = root.Value;
    _ = await store.UpdateAsync(pinned with { ModelUsed = "google/gemini-2.0-flash-001" }, TestContext.Current.CancellationToken);
    store.Updated.Clear();

    (ModelConfig config, string? notice) = await resolver.ResolveAsync(requireVision: false, ct: TestContext.Current.CancellationToken);

    Assert.Equal("google/gemini-2.0-flash-001", config.ModelId);
    Assert.Null(notice);
    Assert.Equal(0, selector.Calls);
    Assert.Empty(store.Updated);
  }

  [Fact]
  public async Task PinnedSession_FallbackStamp_TreatedUnpinned()
  {
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    FakeModelSelector selector = new(Selection("anthropic/claude-3.5-sonnet"));
    RootAgentResolver resolver = new(Ctx(store, rootId), selector);
    Result<AgentRecord> root = await store.GetAsync(rootId, TestContext.Current.CancellationToken);
    Assert.True(root.IsSuccess);
    AgentRecord pinned = root.Value;
    _ = await store.UpdateAsync(pinned with { ModelUsed = FallbackModel }, TestContext.Current.CancellationToken);
    store.Updated.Clear();

    (ModelConfig config, _) = await resolver.ResolveAsync(requireVision: false, ct: TestContext.Current.CancellationToken);

    Assert.Equal("anthropic/claude-3.5-sonnet", config.ModelId);
    Assert.Equal(1, selector.Calls);
  }

  [Fact]
  public async Task SelectionFailure_FallsBack_StaysUnpinned()
  {
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    FailingModelSelector selector = new();
    RootAgentResolver resolver = new(Ctx(store, rootId), selector);

    (ModelConfig config, string? notice) = await resolver.ResolveAsync(requireVision: false, ct: TestContext.Current.CancellationToken);

    Assert.Equal(FallbackModel, config.ModelId);
    Assert.NotNull(notice);
    Assert.Contains("Model selection failed: boom", notice, StringComparison.Ordinal);
    Assert.Contains($"using {FallbackModel}", notice, StringComparison.Ordinal);
    Assert.Empty(store.Updated);
  }

  [Fact]
  public async Task RetryAfterFailure_SelectsOnNextTurn()
  {
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    FailingModelSelector selector = new();
    RootAgentResolver resolver = new(Ctx(store, rootId), selector);

    _ = await resolver.ResolveAsync(requireVision: false, ct: TestContext.Current.CancellationToken);
    FakeModelSelector recovered = new(Selection("google/gemini-2.0-flash-001"));
    RootAgentResolver recoveredResolver = new(Ctx(store, rootId), recovered);

    (ModelConfig config, _) = await recoveredResolver.ResolveAsync(requireVision: false, ct: TestContext.Current.CancellationToken);

    Assert.Equal("google/gemini-2.0-flash-001", config.ModelId);
    Assert.Equal(1, recovered.Calls);
  }

  [Fact]
  public async Task PreferenceOverride_WinsOverPin()
  {
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    FakeModelSelector selector = new(Selection("a"));
    SessionModelPreferences preferences = new() { ModelId = "user-pick" };
    RootAgentResolver resolver = new(Ctx(store, rootId), selector, preferences);
    Result<AgentRecord> root = await store.GetAsync(rootId, TestContext.Current.CancellationToken);
    Assert.True(root.IsSuccess);
    AgentRecord pinned = root.Value;
    _ = await store.UpdateAsync(pinned with { ModelUsed = "pinned-id" }, TestContext.Current.CancellationToken);

    (ModelConfig config, _) = await resolver.ResolveAsync(requireVision: false, ct: TestContext.Current.CancellationToken);

    Assert.Equal("user-pick", config.ModelId);
    Assert.Equal(0, selector.Calls);
  }

  [Fact]
  public async Task OptionsFunc_InvokedOncePerSelection()
  {
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    FakeModelSelector selector = new(Selection("a"));
    int optionCalls = 0;
    RootModelContext ctx = new(store, Identity(rootId), FallbackModel, 2048, 0.7f, new FixedWindowSource(), Catalog: null,
        SmartOptions: () =>
        {
          optionCalls++;
          return SmartSelectionOptions.Default;
        });
    RootAgentResolver resolver = new(ctx, selector);

    _ = await resolver.ResolveAsync(requireVision: false, ct: TestContext.Current.CancellationToken);

    Assert.Equal(1, optionCalls);
  }
}
