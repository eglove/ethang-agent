using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Model.Domain.Tests;

public class SmartModelSelectorTests
{
  private static ModelProviderEntry Entry(
      string modelId, string providerName = "Prov",
      decimal input = 1m, decimal output = 2m, int context = 128_000,
      bool tools = true, bool vision = false,
      double? intel = null, double? coding = null, double? agentic = null) =>
      new(modelId, providerName, input / 1_000_000m, output / 1_000_000m, context, 8_192,
          tools, vision, intel, coding, agentic, null, null, null);

  private static SmartSelectionOptions Options(
      decimal? maxIn = null, decimal? maxOut = null,
      double? minI = null, double? minC = null, double? minA = null,
      int? minCtx = null, SmartSortCriterion sort = SmartSortCriterion.Price) =>
      new(maxIn, maxOut, minI, minC, minA, minCtx, sort);

  private sealed class Catalog(params ModelProviderEntry[] entries) : IModelCatalog
  {
    public Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<ModelProviderEntry>>(entries));
  }

  private sealed class BrokenCatalog : IModelCatalog
  {
    public Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Failure<IReadOnlyList<ModelProviderEntry>>(new DomainError("CatalogDown", "boom")));
  }

  [Fact]
  public async Task PriceSort_PicksCheapestBlended()
  {
    SmartModelSelector selector = new(new Catalog(
        Entry("a", input: 1m, output: 2m),   // blended 1 + 6 = 7
        Entry("b", input: 3m, output: 1m))); // blended 3 + 3 = 6 <- wins
    Result<ModelSelectionResult> result = await selector.SelectAsync(Options(), requireVision: false, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("b", result.Value.ModelId);
    Assert.Contains("cheapest of 2 qualifying models", result.Value.Reasoning, StringComparison.Ordinal);
  }

  [Fact]
  public async Task IntelligenceSort_PicksHighest_NullRanksLast()
  {
    SmartModelSelector selector = new(new Catalog(
        Entry("x", intel: null),
        Entry("y", intel: 40.0),
        Entry("z", intel: 55.0)));
    Result<ModelSelectionResult> result = await selector.SelectAsync(Options(sort: SmartSortCriterion.Intelligence), requireVision: false, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("z", result.Value.ModelId);
    Assert.Contains("Intelligence of 3 qualifying models", result.Value.Reasoning, StringComparison.Ordinal);
  }

  [Fact]
  public async Task CodingSort_PicksHighest()
  {
    SmartModelSelector selector = new(new Catalog(
        Entry("a", coding: 10.0),
        Entry("b", coding: 80.0)));
    Result<ModelSelectionResult> result = await selector.SelectAsync(Options(sort: SmartSortCriterion.Coding), requireVision: false, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("b", result.Value.ModelId);
  }

  [Fact]
  public async Task AgenticSort_PicksHighest()
  {
    SmartModelSelector selector = new(new Catalog(
        Entry("a", agentic: 5.0),
        Entry("b", agentic: 57.0)));
    Result<ModelSelectionResult> result = await selector.SelectAsync(Options(sort: SmartSortCriterion.Agentic), requireVision: false, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("b", result.Value.ModelId);
  }

  [Fact]
  public async Task MaxInputPrice_FiltersOver()
  {
    SmartModelSelector selector = new(new Catalog(
        Entry("cheap", input: 1m),
        Entry("pricey", input: 5m)));
    Result<ModelSelectionResult> result = await selector.SelectAsync(Options(maxIn: 2m), requireVision: false, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("cheap", result.Value.ModelId);
  }

  [Fact]
  public async Task MaxOutputPrice_FiltersOver()
  {
    SmartModelSelector selector = new(new Catalog(
        Entry("cheap", output: 1m),
        Entry("pricey", output: 9m)));
    Result<ModelSelectionResult> result = await selector.SelectAsync(Options(maxOut: 2m), requireVision: false, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("cheap", result.Value.ModelId);
  }

  [Fact]
  public async Task MinScore_SetFloor_FailsNullScore()
  {
    SmartModelSelector selector = new(new Catalog(
        Entry("unknown", intel: null),
        Entry("weak", intel: 25.0)));
    Result<ModelSelectionResult> result = await selector.SelectAsync(Options(minI: 20.0), requireVision: false, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("weak", result.Value.ModelId); // null score fails the floor
  }

  [Fact]
  public async Task MinContext_SetFloor_FailsZero()
  {
    SmartModelSelector selector = new(new Catalog(
        Entry("zero", context: 0),
        Entry("small", context: 16_384)));
    Result<ModelSelectionResult> result = await selector.SelectAsync(Options(minCtx: 8_192), requireVision: false, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("small", result.Value.ModelId);
  }

  [Fact]
  public async Task BatchAndFreeVariants_AlwaysExcluded()
  {
    SmartModelSelector selector = new(new Catalog(
        Entry("model:batch", input: 0.01m),
        Entry("model:free", input: 0m),
        Entry("model")));
    Result<ModelSelectionResult> result = await selector.SelectAsync(Options(), requireVision: false, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("model", result.Value.ModelId);
  }

  [Fact]
  public async Task OpenRouterNamespace_AlwaysExcluded()
  {
    SmartModelSelector selector = new(new Catalog(
        Entry("openrouter/auto", input: 0.01m),
        Entry("real")));
    Result<ModelSelectionResult> result = await selector.SelectAsync(Options(), requireVision: false, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("real", result.Value.ModelId);
  }

  [Fact]
  public async Task ToolUse_AlwaysRequired()
  {
    SmartModelSelector selector = new(new Catalog(
        Entry("notools", tools: false),
        Entry("tools", tools: true)));
    Result<ModelSelectionResult> result = await selector.SelectAsync(Options(), requireVision: false, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("tools", result.Value.ModelId);
  }

  [Fact]
  public async Task RequireVision_FiltersTextOnly()
  {
    SmartModelSelector selector = new(new Catalog(
        Entry("text", vision: false),
        Entry("vision", vision: true)));
    Result<ModelSelectionResult> result = await selector.SelectAsync(Options(), requireVision: true, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("vision", result.Value.ModelId);
  }

  [Fact]
  public async Task NoVisionRequired_TextOnlyAllowed()
  {
    SmartModelSelector selector = new(new Catalog(Entry("text", vision: false)));
    Result<ModelSelectionResult> result = await selector.SelectAsync(Options(), requireVision: false, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("text", result.Value.ModelId);
  }

  [Fact]
  public async Task ZeroQualifiers_Fails()
  {
    SmartModelSelector selector = new(new Catalog(Entry("x", tools: false)));
    Result<ModelSelectionResult> result = await selector.SelectAsync(Options(), requireVision: false, ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("NoMatchingModels", result.Error.Code);
    Assert.Equal("No models match the smart selection constraints.", result.Error.Message);
  }

  [Fact]
  public async Task CatalogFailure_Propagates()
  {
    SmartModelSelector selector = new(new BrokenCatalog());
    Result<ModelSelectionResult> result = await selector.SelectAsync(Options(), requireVision: false, ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("CatalogDown", result.Error.Code);
  }

  [Fact]
  public async Task NegativeSentinelPrice_AlwaysExcluded()
  {
    // OpenRouter's router pseudo-models serve -1 per-token prices ("priced at
    // routing time"). A negative price passes every cap and wins every sort
    // (blended -4), so an unpriced router would beat every real model - the
    // 2026-10-11 jev-router incident. Excluded outright.
    SmartModelSelector selector = new(new Catalog(
        Entry("typesafe/jev-router", input: -1m, output: -1m),
        Entry("real", input: 5m, output: 20m)));
    Result<ModelSelectionResult> result = await selector.SelectAsync(Options(), requireVision: false, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("real", result.Value.ModelId);
  }

  [Fact]
  public async Task NegativeSentinelPrice_ExcludedEvenUnderCaps()
  {
    // The sentinel also survives price caps: -1 <= any cap. The exclusion must
    // hold under the user's constraints too, not just the unconstrained sort.
    SmartModelSelector selector = new(new Catalog(
        Entry("router", input: -1m, output: -1m),
        Entry("real", input: 0.05m, output: 0.5m)));
    Result<ModelSelectionResult> result = await selector.SelectAsync(Options(maxIn: 0.1m, maxOut: 2m), requireVision: false, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("real", result.Value.ModelId);
  }

  [Fact]
  public async Task NegativeSentinelPrice_AllExcluded_Fails()
  {
    SmartModelSelector selector = new(new Catalog(
        Entry("typesafe/jev-router", input: -1m, output: -1m),
        Entry("nvidia/switchyard", input: -1m, output: -1m)));
    Result<ModelSelectionResult> result = await selector.SelectAsync(Options(), requireVision: false, ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("NoMatchingModels", result.Error.Code);
  }

  [Fact]
  public async Task NullProvider_PropagatesFromSelection()
  {
    // A synthesized catalog row (no known endpoints) carries a null provider;
    // selection must pass it through so the resolver never pins the wire.
    Catalog catalog = new(new ModelProviderEntry("some/router", null, 1m / 1_000_000m, 2m / 1_000_000m, 128_000, 8_192, true, false, null, null, null, null, null, null));
    SmartModelSelector nullProviderSelector = new(catalog);
    Result<ModelSelectionResult> result = await nullProviderSelector.SelectAsync(Options(), requireVision: false, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("some/router", result.Value.ModelId);
    Assert.Null(result.Value.ProviderName);
  }

  [Fact]
  public void QualifyingEntries_MatchesSelectFilter_ExcludesVariantsPseudoAndUnscored()
  {
    // The extracted filter chain must exclude the same entries Select excludes:
    // :free variants, the openrouter/ pseudo-namespace, and (under a set score
    // floor) a model below the floor.
    ModelProviderEntry[] entries =
    [
      Entry("glm-5.3-flash", intel: 50.0, coding: 80.0),
      Entry("glm-5.3-flash:free", intel: 50.0, coding: 80.0),
      Entry("openrouter/auto", input: -1m, output: -1m),
      Entry("glm-4.6", intel: 18.5, coding: 45.8),
    ];
    SmartSelectionOptions options = Options(minC: 50.0);

    IReadOnlyList<ModelProviderEntry> qualifiers =
        SmartModelSelectorCore.QualifyingEntries(entries, options, requireVision: false);

    ModelProviderEntry only = Assert.Single(qualifiers);
    Assert.Equal("glm-5.3-flash", only.ModelId);

    // Same filter as Select: the reported qualifying count matches.
    Result<ModelSelectionResult> selected = SmartModelSelectorCore.Select(entries, options, requireVision: false);
    Assert.True(selected.IsSuccess);
    Assert.Contains("cheapest of 1 qualifying models", selected.Value.Reasoning, StringComparison.Ordinal);
  }

  [Fact]
  public void Select_WithWeightOverride_PrefersLowOutputProvider()
  {
    // Same model served by two providers: A is input-cheap / output-pricey,
    // B is input-pricey / output-cheap. Prices are per-token; the Entry helper
    // takes $/M, so 0.04m -> 4e-8 per token.
    ModelProviderEntry a = Entry("m/x", providerName: "A", input: 0.04m, output: 0.5m, intel: 50.0, coding: 80.0);
    ModelProviderEntry b = Entry("m/x", providerName: "B", input: 0.07m, output: 0.05m, intel: 50.0, coding: 80.0);
    ModelProviderEntry[] entries = [a, b];

    // Default weight (3) favors cheap output -> B.
    Result<ModelSelectionResult> byDefault = SmartModelSelectorCore.Select(entries, Options(), requireVision: false);
    Assert.True(byDefault.IsSuccess);
    Assert.Equal("B", byDefault.Value.ProviderName);

    // An input-heavy override (below the 0.0667 crossover) favors cheap input -> A.
    Result<ModelSelectionResult> inputHeavy =
        SmartModelSelectorCore.Select(entries, Options(), requireVision: false, outputWeightOverride: 0.02m);
    Assert.True(inputHeavy.IsSuccess);
    Assert.Equal("A", inputHeavy.Value.ProviderName);
  }

  [Fact]
  public async Task Determinism_SameCatalogSameOptions_SameResult()
  {
    Catalog catalog = new(
        Entry("a", input: 2m, output: 2m),
        Entry("b", input: 2m, output: 2m)); // price tie -> ModelId ordinal breaks it
    SmartModelSelector selector = new(catalog);
    Result<ModelSelectionResult> first = await selector.SelectAsync(Options(), requireVision: false, ct: TestContext.Current.CancellationToken);
    Result<ModelSelectionResult> second = await selector.SelectAsync(Options(), requireVision: false, ct: TestContext.Current.CancellationToken);

    Assert.True(first.IsSuccess && second.IsSuccess);
    Assert.Equal(first.Value.ModelId, second.Value.ModelId);
    Assert.Equal("a", first.Value.ModelId); // ordinal: a < b
  }
}
