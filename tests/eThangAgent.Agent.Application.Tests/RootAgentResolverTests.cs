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

  /// <summary>A window source that knows NO model - the degraded catalog shape.</summary>
  private sealed class UnknownWindowSource : IContextWindowSource
  {
    public Task<int?> WindowForAsync(string modelId, string? providerName, CancellationToken ct = default)
        => Task.FromResult<int?>(null);
  }

  [Fact]
  public async Task UnpinnedSession_FailsNoModelSelected_NeverSubstitutesTheFallback()
  {
    // 2026-10-22: an unpinned session used to be served openrouter/auto SILENTLY. That is
    // the substitution this test forbids: the user never chose it, and the app displayed
    // something else.
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    RootAgentResolver resolver = new(Ctx(store, rootId));

    Result<(ModelConfig Config, string? Notice)> resolution =
        await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.False(resolution.IsSuccess);
    Assert.Equal("NoModelSelected", resolution.Error.Code);
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

    Result<(ModelConfig Config, string? Notice)> resolution =
        await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.True(resolution.IsSuccess, resolution.Error?.Message);
    Assert.Equal("google/gemini-2.0-flash-001", resolution.Value.Config.ModelId);
    Assert.Null(resolution.Value.Notice);
    Assert.Empty(store.Updated);
  }

  [Fact]
  public async Task PinnedSession_FallbackStamp_TreatedUnpinned_AndFails()
  {
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    Result<AgentRecord> root = await store.GetAsync(rootId, TestContext.Current.CancellationToken);
    Assert.True(root.IsSuccess);
    _ = await store.UpdateAsync(root.Value with { ModelUsed = FallbackModel }, TestContext.Current.CancellationToken);
    store.Updated.Clear();
    RootAgentResolver resolver = new(Ctx(store, rootId));

    Result<(ModelConfig Config, string? Notice)> resolution =
        await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.False(resolution.IsSuccess);
    Assert.Equal("NoModelSelected", resolution.Error.Code);
    Assert.Empty(store.Updated);
  }

  [Fact]
  public async Task ClearedPin_FailsNoModelSelected_OnNextTurn()
  {
    // The Auto model choice clears the persisted pin (ModelUsed -> unassigned). The next
    // resolution then has nothing to serve and says so instead of serving the fallback.
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    Result<AgentRecord> root = await store.GetAsync(rootId, TestContext.Current.CancellationToken);
    Assert.True(root.IsSuccess);
    _ = await store.UpdateAsync(root.Value with { ModelUsed = "~z-ai/glm-flash-latest" }, TestContext.Current.CancellationToken);
    RootAgentResolver pinnedResolver = new(Ctx(store, rootId));
    Result<(ModelConfig Config, string? Notice)> pinned =
        await pinnedResolver.ResolveAsync(ct: TestContext.Current.CancellationToken);
    Assert.True(pinned.IsSuccess, pinned.Error?.Message);
    Assert.Equal("~z-ai/glm-flash-latest", pinned.Value.Config.ModelId); // the stale pin serves

    Result<AgentRecord> afterPin = await store.GetAsync(rootId, TestContext.Current.CancellationToken);
    Assert.True(afterPin.IsSuccess);
    _ = await store.UpdateAsync(afterPin.Value with { ModelUsed = AgentRecord.UnassignedModel }, TestContext.Current.CancellationToken);
    store.Updated.Clear();
    RootAgentResolver resolver = new(Ctx(store, rootId));

    Result<(ModelConfig Config, string? Notice)> resolution =
        await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.False(resolution.IsSuccess);
    Assert.Equal("NoModelSelected", resolution.Error.Code);
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

    Result<(ModelConfig Config, string? Notice)> resolution =
        await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.True(resolution.IsSuccess, resolution.Error?.Message);
    Assert.Equal("user-pick", resolution.Value.Config.ModelId);
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

    Result<(ModelConfig Config, string? Notice)> resolution =
        await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.True(resolution.IsSuccess, resolution.Error?.Message);
    Assert.Equal("user-pick", resolution.Value.Config.ModelId);
    Assert.Null(resolution.Value.Notice);
    Assert.Empty(store.Updated);
  }

  [Fact]
  public async Task PreferredModelWithUnknownWindow_FailsUnknownModelWindow_NeverSubstitutes()
  {
    // The exact production symptom: the user picked minimax/minimax-m3.1-flash:free, the
    // catalog could not resolve its window, and the resolver silently served openrouter/auto
    // instead - so the app displayed minimax while the turns ran on the router.
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    SessionModelPreferences preferences = new() { ModelId = "minimax/minimax-m3.1-flash:free" };
    RootAgentResolver resolver = new(
        new RootModelContext(store, Identity(rootId), FallbackModel, 2048, 0.7f, new UnknownWindowSource()),
        preferences);

    Result<(ModelConfig Config, string? Notice)> resolution =
        await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.False(resolution.IsSuccess);
    Assert.Equal("UnknownModelWindow", resolution.Error.Code);
    Assert.Contains("minimax/minimax-m3.1-flash:free", resolution.Error.Message, StringComparison.Ordinal);
    Assert.Empty(store.Updated);
  }

  [Fact]
  public async Task TheCommittedProvider_RidesTheResolvedConfig()
  {
    // Issue #296, D7: the pair is the decision. The resolver used to pass null for the
    // provider on EVERY resolution, so a session whose status line named a provider sent a
    // request that pinned none - a display claiming something the wire never carried.
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    SessionModelPreferences preferences = new()
    {
      ModelId = "deepseek/deepseek-v4.1-flash-1017",
      ProviderName = "DeepInfra",
    };
    RootAgentResolver resolver = new(Ctx(store, rootId), preferences);

    Result<(ModelConfig Config, string? Notice)> resolution =
        await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.True(resolution.IsSuccess, resolution.Error?.Message);
    Assert.Equal("deepseek/deepseek-v4.1-flash-1017", resolution.Value.Config.ModelId);
    Assert.Equal("DeepInfra", resolution.Value.Config.Provider);
  }

  [Fact]
  public async Task AnUncommittedProvider_LeavesTheConfigUnpinned()
  {
    // Null means UNSET, never "no provider exists": a session that committed no provider
    // keeps the provider's own routing, exactly as every pre-existing session does.
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    SessionModelPreferences preferences = new() { ModelId = "alpha/model" };
    RootAgentResolver resolver = new(Ctx(store, rootId), preferences);

    Result<(ModelConfig Config, string? Notice)> resolution =
        await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.True(resolution.IsSuccess, resolution.Error?.Message);
    Assert.Null(resolution.Value.Config.Provider);
  }

  [Fact]
  public async Task APinnedSession_AlsoCarriesTheCommittedProvider()
  {
    // The pin path (no live preference) must not drop the provider: a resumed session serves
    // the same model ON the same provider.
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    Result<AgentRecord> root = await store.GetAsync(rootId, TestContext.Current.CancellationToken);
    Assert.True(root.IsSuccess);
    _ = await store.UpdateAsync(root.Value with { ModelUsed = "alpha/model" },
        TestContext.Current.CancellationToken);
    SessionModelPreferences preferences = new() { ProviderName = "DeepInfra" };
    RootAgentResolver resolver = new(Ctx(store, rootId), preferences);

    Result<(ModelConfig Config, string? Notice)> resolution =
        await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.True(resolution.IsSuccess, resolution.Error?.Message);
    Assert.Equal("alpha/model", resolution.Value.Config.ModelId);
    Assert.Equal("DeepInfra", resolution.Value.Config.Provider);
  }

  [Fact]
  public async Task APinnedProviderTheCatalogNoLongerOffers_FailsNamingTheProvider()
  {
    // Issue #297, D5/B5: the pin is a HARD constraint, so a provider that has disappeared from
    // the catalog cannot be served and must not be swapped for one that can. The message names
    // the provider AND the model, because the user has to know which half of the pair to change.
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    SessionModelPreferences preferences = new() { ModelId = "alpha/model", ProviderName = "Reka" };
    RootAgentResolver resolver = new(
        new RootModelContext(store, Identity(rootId), FallbackModel, 2048, 0.7f, new FixedWindowSource(),
            new ListingCatalog(("alpha/model", "DeepInfra"))),
        preferences);

    Result<(ModelConfig Config, string? Notice)> resolution =
        await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.False(resolution.IsSuccess);
    Assert.Equal("UnknownPinnedProvider", resolution.Error.Code);
    Assert.Contains("Reka", resolution.Error.Message, StringComparison.Ordinal);
    Assert.Contains("alpha/model", resolution.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AnUnreadableCatalog_DoesNotFailAPinnedTurn()
  {
    // A failed catalog fetch is an ENVIRONMENTAL fault, never proof that the pin vanished:
    // failing here would tell the user their chosen provider is gone because a fetch timed out.
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    SessionModelPreferences preferences = new() { ModelId = "alpha/model", ProviderName = "DeepInfra" };
    RootAgentResolver resolver = new(
        new RootModelContext(store, Identity(rootId), FallbackModel, 2048, 0.7f, new FixedWindowSource(),
            ListingCatalog.Unavailable()),
        preferences);

    Result<(ModelConfig Config, string? Notice)> resolution =
        await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.True(resolution.IsSuccess, resolution.Error?.Message);
    Assert.Equal("DeepInfra", resolution.Value.Config.Provider);
  }

  [Fact]
  public async Task PinnedModelWithUnknownWindow_FailsUnknownModelWindow()
  {
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    Result<AgentRecord> root = await store.GetAsync(rootId, TestContext.Current.CancellationToken);
    Assert.True(root.IsSuccess);
    _ = await store.UpdateAsync(root.Value with { ModelUsed = "minimax/minimax-m3.1-flash:free" }, TestContext.Current.CancellationToken);
    RootAgentResolver resolver = new(
        new RootModelContext(store, Identity(rootId), FallbackModel, 2048, 0.7f, new UnknownWindowSource()));

    Result<(ModelConfig Config, string? Notice)> resolution =
        await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.False(resolution.IsSuccess);
    Assert.Equal("UnknownModelWindow", resolution.Error.Code);
  }
}
