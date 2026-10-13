using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

public class RootAgentResolverTests
{
  private const string FallbackModel = "openrouter/auto";

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
  public async Task UnpinnedSession_ServesFallback()
  {
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    RootAgentResolver resolver = new(Ctx(store, rootId));

    (ModelConfig config, string? notice) = await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.Equal(FallbackModel, config.ModelId);
    Assert.Null(notice);
    Assert.Empty(store.Updated);
  }

  [Fact]
  public async Task PinnedSession_ServesPin_NeverRewrites()
  {
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    Result<AgentRecord> root = await store.GetAsync(rootId, TestContext.Current.CancellationToken);
    Assert.True(root.IsSuccess);
    _ = await store.UpdateAsync(root.Value with { ModelUsed = "google/gemini-2.0-flash-001" }, TestContext.Current.CancellationToken);
    store.Updated.Clear();
    RootAgentResolver resolver = new(Ctx(store, rootId));

    (ModelConfig config, string? notice) = await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.Equal("google/gemini-2.0-flash-001", config.ModelId);
    Assert.Null(notice);
    Assert.Empty(store.Updated);
  }

  [Fact]
  public async Task PinnedSession_FallbackStamp_TreatedUnpinned()
  {
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    Result<AgentRecord> root = await store.GetAsync(rootId, TestContext.Current.CancellationToken);
    Assert.True(root.IsSuccess);
    _ = await store.UpdateAsync(root.Value with { ModelUsed = FallbackModel }, TestContext.Current.CancellationToken);
    store.Updated.Clear();
    RootAgentResolver resolver = new(Ctx(store, rootId));

    (ModelConfig config, _) = await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.Equal(FallbackModel, config.ModelId);
    Assert.Empty(store.Updated);
  }

  [Fact]
  public async Task ClearedPin_ServesFallback_OnNextTurn()
  {
    // The Auto model choice clears the persisted pin (ModelUsed -> unassigned). The
    // next resolution then serves the fallback instead of the stale pin.
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    Result<AgentRecord> root = await store.GetAsync(rootId, TestContext.Current.CancellationToken);
    Assert.True(root.IsSuccess);
    _ = await store.UpdateAsync(root.Value with { ModelUsed = "~z-ai/glm-flash-latest" }, TestContext.Current.CancellationToken);
    RootAgentResolver pinnedResolver = new(Ctx(store, rootId));
    (ModelConfig pinnedConfig, _) = await pinnedResolver.ResolveAsync(ct: TestContext.Current.CancellationToken);
    Assert.Equal("~z-ai/glm-flash-latest", pinnedConfig.ModelId); // the stale pin serves

    Result<AgentRecord> afterPin = await store.GetAsync(rootId, TestContext.Current.CancellationToken);
    Assert.True(afterPin.IsSuccess);
    _ = await store.UpdateAsync(afterPin.Value with { ModelUsed = AgentRecord.UnassignedModel }, TestContext.Current.CancellationToken);
    store.Updated.Clear();
    RootAgentResolver resolver = new(Ctx(store, rootId));

    (ModelConfig config, string? notice) = await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.Equal(FallbackModel, config.ModelId);
    Assert.Null(notice);
    Assert.Empty(store.Updated);
  }

  [Fact]
  public async Task PreferenceOverride_WinsOverPin()
  {
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    SessionModelPreferences preferences = new() { ModelId = "user-pick" };
    RootAgentResolver resolver = new(Ctx(store, rootId), preferences);
    Result<AgentRecord> root = await store.GetAsync(rootId, TestContext.Current.CancellationToken);
    Assert.True(root.IsSuccess);
    AgentRecord pinned = root.Value;
    _ = await store.UpdateAsync(pinned with { ModelUsed = "pinned-id" }, TestContext.Current.CancellationToken);
    store.Updated.Clear();

    (ModelConfig config, _) = await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.Equal("user-pick", config.ModelId);
    AgentRecord updated = Assert.Single(store.Updated);
    Assert.Equal("user-pick", updated.ModelUsed);
  }

  [Fact]
  public async Task PreferencePersist_NoOp_WhenAlreadyPinned()
  {
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    SessionModelPreferences preferences = new() { ModelId = "user-pick" };
    RootAgentResolver resolver = new(Ctx(store, rootId), preferences);
    Result<AgentRecord> seeded = await store.GetAsync(rootId, TestContext.Current.CancellationToken);
    Assert.True(seeded.IsSuccess);
    _ = await store.UpdateAsync(seeded.Value with { ModelUsed = "user-pick" }, TestContext.Current.CancellationToken);
    store.Updated.Clear();

    (ModelConfig config, string? notice) = await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.Equal("user-pick", config.ModelId);
    Assert.Null(notice);
    Assert.Empty(store.Updated);
  }
}
