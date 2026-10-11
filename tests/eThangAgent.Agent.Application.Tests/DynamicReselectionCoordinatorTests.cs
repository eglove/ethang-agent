using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>The safe-point reselection coordinator: gates on an explicit pick and a
/// non-price sort, re-selects with the measured weight at a price crossing, rewrites
/// the pin, emits the notice, and appends the audit row - degrading to silence on
/// every failure branch.</summary>
public class DynamicReselectionCoordinatorTests
{
  private const string FallbackModel = "openrouter/auto";

  /// <summary>Catalog fake counting GetAsync calls; can be made to fail.</summary>
  private sealed class CountingCatalog(params ModelProviderEntry[] entries) : IModelCatalog
  {
    public int Calls { get; private set; }

    public bool Fail { get; set; }

    public Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default)
    {
      Calls++;
      return Task.FromResult(Fail
          ? Result.Failure<IReadOnlyList<ModelProviderEntry>>(new DomainError("CatalogFailed", "boom"))
          : Result.Success<IReadOnlyList<ModelProviderEntry>>(entries));
    }
  }

  // The Task 3 P() helper shape: prices in $/M converted to per-token, model id "m/x".
  private static ModelProviderEntry P(string prov, decimal inM, decimal outM) =>
      new("m/x", prov, inM / 1_000_000m, outM / 1_000_000m, 128000, 0, true, false, 50, 80, null, null, null, null);

  private static async Task<AgentId> SeedRootAsync(FakeAgentStore store, string? modelUsed = null)
  {
    AgentId rootId = AgentId.NewId();
    _ = await store.SaveAsync(AgentRecord.Root(rootId, DateTimeOffset.UtcNow, "C:/workspaces/demo", "openrouter", modelUsed)).ConfigureAwait(false);
    return rootId;
  }

  private static RootSessionIdentity Identity(AgentId rootId) => new() { Id = rootId };

  private static DynamicReselectionCoordinator Coordinator(
      IModelCatalog catalog,
      MeasuredMixPolicy policy,
      FakeAgentStore? store = null,
      RootSessionIdentity? identity = null,
      SessionModelPreferences? preferences = null,
      SmartSelectionOptions? options = null)
      => new(catalog, () => options ?? SmartSelectionOptions.Default, preferences, policy, store, identity, FallbackModel);

  /// <summary>A trusted policy whose measured weight has risen across the A/B
  /// break-even (w = 0.0667): at the seed weight A is cheapest, at the measured
  /// weight B is - exactly the crossing that re-selects toward B.</summary>
  private static MeasuredMixPolicy CrossedPolicy()
  {
    MeasuredMixPolicy policy = new(0.05m);
    for (int i = 0; i < 5; i++)
    {
      policy.Observe(new UsageObservation(100, 400));
    }

    return policy;
  }

  /// <summary>A trusted policy whose measured weight still ranks B cheapest at both
  /// the seed and the measured weight - no crossing.</summary>
  private static MeasuredMixPolicy TrustedNoCrossPolicy()
  {
    MeasuredMixPolicy policy = new(3.0m);
    for (int i = 0; i < 5; i++)
    {
      policy.Observe(new UsageObservation(100, 1));
    }

    return policy;
  }

  [Fact]
  public async Task Apply_ExplicitPick_NeverTouchesCatalog()
  {
    CountingCatalog catalog = new(P("A", 0.04m, 0.50m), P("B", 0.07m, 0.05m));
    SessionModelPreferences preferences = new() { ModelId = "user-pick" };
    DynamicReselectionCoordinator coordinator = Coordinator(catalog, CrossedPolicy(), preferences: preferences);

    (bool applied, string? notice) = await coordinator.ApplyAsync(requireVision: false, ct: TestContext.Current.CancellationToken);

    Assert.False(applied);
    Assert.Null(notice);
    Assert.Equal(0, catalog.Calls);
  }

  [Fact]
  public async Task Apply_NonPriceSort_NeverTouchesCatalog()
  {
    CountingCatalog catalog = new(P("A", 0.04m, 0.50m), P("B", 0.07m, 0.05m));
    SmartSelectionOptions options = new(null, null, null, null, null, null, SmartSortCriterion.Intelligence);
    DynamicReselectionCoordinator coordinator = Coordinator(catalog, CrossedPolicy(), options: options);

    (bool applied, string? notice) = await coordinator.ApplyAsync(requireVision: false, ct: TestContext.Current.CancellationToken);

    Assert.False(applied);
    Assert.Null(notice);
    Assert.Equal(0, catalog.Calls);
  }

  [Fact]
  public async Task Apply_CatalogFails_PinStays()
  {
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store, "old/model");
    CountingCatalog catalog = new(P("A", 0.04m, 0.50m), P("B", 0.07m, 0.05m)) { Fail = true };
    DynamicReselectionCoordinator coordinator = Coordinator(catalog, CrossedPolicy(), store, Identity(rootId));

    (bool applied, string? notice) = await coordinator.ApplyAsync(requireVision: false, ct: TestContext.Current.CancellationToken);

    Assert.False(applied);
    Assert.Null(notice);
    Assert.Empty(store.Updated);
  }

  [Fact]
  public async Task Apply_NoCrossing_SilentNoWrites()
  {
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store, "old/model");
    CountingCatalog catalog = new(P("A", 0.04m, 0.50m), P("B", 0.07m, 0.05m));
    DynamicReselectionCoordinator coordinator = Coordinator(catalog, TrustedNoCrossPolicy(), store, Identity(rootId));

    (bool applied, string? notice) = await coordinator.ApplyAsync(requireVision: false, ct: TestContext.Current.CancellationToken);

    Assert.False(applied);
    Assert.Null(notice);
    Assert.Empty(store.Updated);
    Result<IReadOnlyList<Message>> transcript = await store.GetTranscriptAsync(rootId, TestContext.Current.CancellationToken);
    Assert.True(transcript.IsSuccess);
    Assert.Empty(transcript.Value);
  }

  [Fact]
  public async Task Apply_Crossing_RewritesPin_NoticeAndAudit()
  {
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store, "old/model");
    CountingCatalog catalog = new(P("A", 0.04m, 0.50m), P("B", 0.07m, 0.05m));
    DynamicReselectionCoordinator coordinator = Coordinator(catalog, CrossedPolicy(), store, Identity(rootId));

    (bool applied, string? notice) = await coordinator.ApplyAsync(requireVision: false, ct: TestContext.Current.CancellationToken);

    Assert.True(applied);
    Assert.NotNull(notice);
    Assert.Contains("Model selected: m/x via B", notice, StringComparison.Ordinal);
    AgentRecord updated = Assert.Single(store.Updated);
    Assert.Equal("m/x", updated.ModelUsed);
    Result<IReadOnlyList<Message>> transcript = await store.GetTranscriptAsync(rootId, TestContext.Current.CancellationToken);
    Assert.True(transcript.IsSuccess);
    Message audit = Assert.Single(transcript.Value);
    Assert.Equal(Role.System, audit.Role);
    Assert.Contains("[reselection]", audit.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Apply_SelectFails_PinStays_NoNotice()
  {
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store, "old/model");
    CountingCatalog catalog = new(P("A", 0.04m, 0.50m), P("B", 0.07m, 0.05m));
    SmartSelectionOptions options = new(null, null, 999d, null, null, null, SmartSortCriterion.Price);
    DynamicReselectionCoordinator coordinator = Coordinator(catalog, CrossedPolicy(), store, Identity(rootId), options: options);

    (bool applied, string? notice) = await coordinator.ApplyAsync(requireVision: false, ct: TestContext.Current.CancellationToken);

    Assert.False(applied);
    Assert.Null(notice);
    Assert.Empty(store.Updated);
  }
}
