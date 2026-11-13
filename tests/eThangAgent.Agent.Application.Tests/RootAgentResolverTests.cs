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
    public Task<int?> WindowForAsync(string modelId, CancellationToken ct = default)
        => Task.FromResult<int?>(null);
  }

  /// <summary>A window source that knows exactly one model, so a turn's failure can be attributed
  ///     to the head it named rather than to a catalog that knows nothing.</summary>
  private sealed class OnlyOneModelKnownWindowSource(string knownModelId) : IContextWindowSource
  {
    public Task<int?> WindowForAsync(string modelId, CancellationToken ct = default)
        => Task.FromResult(modelId == knownModelId ? 64_000 : (int?)null);
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
    SessionModelPreferences preferences = new() { ModelIds = ["user-pick"] };
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
    SessionModelPreferences preferences = new() { ModelIds = ["user-pick"] };
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
    SessionModelPreferences preferences = new() { ModelIds = ["minimax/minimax-m3.1-flash:free"] };
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
  public async Task TheCommittedVariant_CarriesTheSuffixOnTheWireModelId()
  {
    // THE VARIANT IS THE ROUTING DECISION, and it reaches the config as a suffix on the model id
    // - the one field the provider routes on. A resolver that returned the bare model while the
    // status line promised nitro would be the same class of lie the 2026-10-22 substitution was:
    // the display naming something the wire never carried.
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    SessionModelPreferences preferences = new()
    {
      ModelIds = ["deepseek/deepseek-v4.1-flash-1017"],
      ModelVariant = ModelVariant.Nitro,
    };
    RootAgentResolver resolver = new(Ctx(store, rootId), preferences);

    Result<(ModelConfig Config, string? Notice)> resolution =
        await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.True(resolution.IsSuccess, resolution.Error?.Message);
    Assert.Equal("deepseek/deepseek-v4.1-flash-1017:nitro", resolution.Value.Config.ModelId);
  }

  [Fact]
  public async Task AnUncommittedVariant_LeavesTheWireModelIdBare()
  {
    // Null means UNSET, never 'standard was chosen': a session that committed no variant keeps
    // the provider's own routing, exactly as every pre-existing session does.
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    SessionModelPreferences preferences = new() { ModelIds = ["alpha/model"] };
    RootAgentResolver resolver = new(Ctx(store, rootId), preferences);

    Result<(ModelConfig Config, string? Notice)> resolution =
        await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.True(resolution.IsSuccess, resolution.Error?.Message);
    Assert.Equal("alpha/model", resolution.Value.Config.ModelId);
  }

  [Fact]
  public async Task APinnedSession_AlsoCarriesTheCommittedVariant()
  {
    // The pin path (no live model preference) must not drop the variant: a resumed session
    // serves the same model under the same tradeoff. This is ALSO the regression guard for the
    // catalog lookup - the persisted pin may carry the suffix, and looking the suffixed id up
    // would fail the turn with 'no catalog context window' for a model that plainly exists.
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    Result<AgentRecord> root = await store.GetAsync(rootId, TestContext.Current.CancellationToken);
    Assert.True(root.IsSuccess);
    _ = await store.UpdateAsync(root.Value with { ModelUsed = "alpha/model:nitro" },
        TestContext.Current.CancellationToken);
    SessionModelPreferences preferences = new() { ModelVariant = ModelVariant.Nitro };
    RootAgentResolver resolver = new(Ctx(store, rootId), preferences);

    Result<(ModelConfig Config, string? Notice)> resolution =
        await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.True(resolution.IsSuccess, resolution.Error?.Message);
    Assert.Equal("alpha/model:nitro", resolution.Value.Config.ModelId);
  }

  [Fact]
  public async Task EveryVariant_ServesAModelTheCatalogLists()
  {
    // THE RETIRED UnknownPinnedProvider HAS NOTHING LEFT TO CHECK. A variant sorts the model's
    // whole eligible pool and never narrows it, so a model the catalog lists is servable under
    // all three variants - there is no 'the provider you chose no longer serves this model'
    // state for a turn to fail on.
    foreach (ModelVariant variant in ModelVariants.All)
    {
      FakeAgentStore store = new();
      AgentId rootId = await SeedRootAsync(store);
      SessionModelPreferences preferences = new() { ModelIds = ["alpha/model"], ModelVariant = variant };
      RootAgentResolver resolver = new(
          new RootModelContext(store, Identity(rootId), FallbackModel, 2048, 0.7f, new FixedWindowSource(),
              new ListingCatalog(("alpha/model", "DeepInfra"))),
          preferences);

      Result<(ModelConfig Config, string? Notice)> resolution =
          await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

      Assert.True(resolution.IsSuccess, resolution.Error?.Message);
    }
  }

  [Fact]
  public async Task AnUnreadableCatalog_DoesNotFailTheTurn()
  {
    // A failed catalog fetch is an ENVIRONMENTAL fault. The window comes from the injected
    // source here, so the turn commits; the catalog's only remaining role is the vision
    // capability, which resolves false rather than failing anything.
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    SessionModelPreferences preferences = new() { ModelIds = ["alpha/model"], ModelVariant = ModelVariant.Nitro };
    RootAgentResolver resolver = new(
        new RootModelContext(store, Identity(rootId), FallbackModel, 2048, 0.7f, new FixedWindowSource(),
            ListingCatalog.Unavailable()),
        preferences);

    Result<(ModelConfig Config, string? Notice)> resolution =
        await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.True(resolution.IsSuccess, resolution.Error?.Message);
    Assert.Equal("alpha/model:nitro", resolution.Value.Config.ModelId);
  }
  [Fact]
  public async Task AnOrderedPreference_ResolvesItsHeadAndCarriesTheRestInOrder()
  {
    // The head is the model that runs and the tail is the ordered fallback the provider falls
    // through, so the list has to survive resolution in the order the user stated it.
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    SessionModelPreferences preferences = new()
    {
      ModelIds = ["alpha/model", "beta/other", "gamma/third"],
    };
    RootAgentResolver resolver = new(Ctx(store, rootId), preferences);

    Result<(ModelConfig Config, string? Notice)> resolution =
        await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.True(resolution.IsSuccess, resolution.Error?.Message);
    Assert.Equal("alpha/model", resolution.Value.Config.ModelId);
    Assert.Equal(["beta/other", "gamma/third"], resolution.Value.Config.FallbackModelIds);
  }

  [Fact]
  public async Task ASingleModelPreference_ResolvesToAnEmptyFallbackList()
  {
    // One entry is every workspace's shape today, and empty is what makes the request carry no
    // models array at all.
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    SessionModelPreferences preferences = new() { ModelIds = ["alpha/model"] };
    RootAgentResolver resolver = new(Ctx(store, rootId), preferences);

    Result<(ModelConfig Config, string? Notice)> resolution =
        await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.True(resolution.IsSuccess, resolution.Error?.Message);
    Assert.Equal("alpha/model", resolution.Value.Config.ModelId);
    Assert.Empty(resolution.Value.Config.FallbackModelIds);
  }

  [Fact]
  public async Task NoLivePreference_ServesThePin_WithNoFallbacks()
  {
    // A resumed session has no live list; the persisted pin is what served and it names ONE
    // model, so the turn must not inherit a list from anywhere.
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    Result<AgentRecord> root = await store.GetAsync(rootId, TestContext.Current.CancellationToken);
    Assert.True(root.IsSuccess);
    _ = await store.UpdateAsync(root.Value with { ModelUsed = "pinned/model" },
        TestContext.Current.CancellationToken);
    RootAgentResolver resolver = new(Ctx(store, rootId));

    Result<(ModelConfig Config, string? Notice)> resolution =
        await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.True(resolution.IsSuccess, resolution.Error?.Message);
    Assert.Equal("pinned/model", resolution.Value.Config.ModelId);
    Assert.Empty(resolution.Value.Config.FallbackModelIds);
  }

  [Fact]
  public async Task NeitherPreferenceNorPin_FailsNoModelSelected_WithNoBackupToTry()
  {
    // Nothing selected is a FAILURE, never a list to fall through: a backup is a model the user
    // named, so there is no list to build when they named none.
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    RootAgentResolver resolver = new(Ctx(store, rootId));

    Result<(ModelConfig Config, string? Notice)> resolution =
        await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.False(resolution.IsSuccess);
    Assert.Equal("NoModelSelected", resolution.Error.Code);
    Assert.Contains("never substitutes one", resolution.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AnUnknownHead_FailsTheTurn_EvenWhenItsBackupsAreKnown()
  {
    // The backups are a ROUTING PREFERENCE, not a rescue: a head the harness cannot budget has no
    // context window, and quietly serving the second entry instead would be the substitution this
    // resolver exists to refuse. The user re-picks; the harness never does.
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    SessionModelPreferences preferences = new()
    {
      ModelIds = ["unknown/model", "beta/other"],
    };
    RootAgentResolver resolver = new(
        new RootModelContext(store, Identity(rootId), FallbackModel, 2048, 0.7f,
            new OnlyOneModelKnownWindowSource("beta/other")),
        preferences);

    Result<(ModelConfig Config, string? Notice)> resolution =
        await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.False(resolution.IsSuccess);
    Assert.Equal("UnknownModelWindow", resolution.Error.Code);
    Assert.Contains("unknown/model", resolution.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AnOrderedPreference_PersistsOnlyItsHeadAsWhatServed()
  {
    // ModelUsed is the record of what actually served ONE turn, and the resolver normalizes a
    // suffixed id to its base. Writing the list there would make 'what served' and 'what was
    // requested' the same field.
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    SessionModelPreferences preferences = new()
    {
      ModelIds = ["alpha/model", "beta/other"],
      ModelVariant = ModelVariant.Nitro,
    };
    RootAgentResolver resolver = new(Ctx(store, rootId), preferences);

    _ = await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    AgentRecord updated = Assert.Single(store.Updated);
    Assert.Equal("alpha/model", updated.ModelUsed);
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
