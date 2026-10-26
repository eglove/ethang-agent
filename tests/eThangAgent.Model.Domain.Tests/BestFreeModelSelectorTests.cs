using eThangAgent.ModelDomain;

namespace eThangAgent.Model.Domain.Tests;

/// <summary>The catalog-driven selection rule behind the live-model suite (issue #289) and,
///     with no change at its seam, behind a future "best free model" option in the model
///     picker: a PURE function over catalog entries, so it is unit tested against a
///     FIXTURE catalog and never touches the network.
///     <para>The fixture below mirrors the LIVE catalog's shape (measured 2026-10-26): the
///     real free models and their scores, the two music generators that advertise zero
///     prices but no tool capability, an unpriced model, the free router, and a paid
///     model. Every rejection the issue names is one row here.</para>
///     <para>THE RULE, in the order it is applied: a model qualifies when both billable
///     prices are PRESENT and zero (unknown pricing is not free), it produces text, it
///     supports tool use, its context window clears the caller's requirement, and it is a
///     MODEL rather than a router. Qualifying models rank by coding, then intelligence,
///     then agentic score, with NULL SCORES LAST - an unmeasured model is not a
///     zero-scoring one.</para></summary>
public class BestFreeModelSelectorTests
{
  /// <summary>One fixture row: by default a plain free, tool-capable text model at the live
  ///     catalog's context window. Tests vary only the fact each is about.</summary>
  private static ModelProviderEntry Entry(string modelId, decimal? pricing = 0m, int contextLength = 262_144)
      => new(modelId, "Vendor", pricing, null, pricing, contextLength, 8_192,
          SupportsToolUse: true, SupportsVision: false, SupportsTextOutput: true,
          IntelligenceScore: null, CodingScore: null, AgenticScore: null,
          LatencyMs: null, ThroughputTokensPerSec: null, Description: null);

  /// <summary>A scored row: the scores are what selection ranks on.</summary>
  private static ModelProviderEntry Scored(string modelId, double? coding, double? intelligence = null,
      double? agentic = null, int contextLength = 262_144, string? provider = "Vendor")
      => Entry(modelId, contextLength: contextLength) with
      {
        ProviderName = provider,
        CodingScore = coding,
        IntelligenceScore = intelligence,
        AgenticScore = agentic,
      };

  /// <summary>The same row with its capability facts flipped: no tool use (the music
  ///     generators), no text output, or a router pseudo-model.</summary>
  private static ModelProviderEntry Capabilities(ModelProviderEntry entry, bool toolUse = true,
      bool textOutput = true, bool isRouter = false)
      => entry with
      {
        SupportsToolUse = toolUse,
        SupportsTextOutput = textOutput,
        IsRouter = isRouter,
      };

  /// <summary>The live free catalog (scores as advertised), plus the rows the rule must
  ///     reject. Deliberately in an arbitrary order: selection must not depend on it.</summary>
  private static IReadOnlyList<ModelProviderEntry> LiveShapedCatalog() =>
  [
      Scored("cohere/north-mini-code:free", coding: 36.5, intelligence: 9.9, agentic: 1.1,
          contextLength: 256_000, provider: "Cohere"),
      Scored("thinkingmachines/inkling:free", coding: 52.1, intelligence: 25.0, agentic: 22.5,
          contextLength: 1_048_576, provider: "Thinking Machines"),
      Scored("thinkingmachines/inkling-small:free", coding: 52.9, intelligence: 25.7, agentic: 23.5,
          contextLength: 1_048_576, provider: "Thinking Machines"),
      Scored("nvidia/nemotron-3-ultra-550b-a55b:free", coding: 49.3, intelligence: 22.9, agentic: 20.1,
          contextLength: 1_000_000, provider: "Nvidia"),
      Scored("google/gemma-4-31b-it:free", coding: 43.4, intelligence: 14.7, agentic: 4.2,
          provider: "Google AI Studio"),
      // Unmeasured (no Artificial Analysis scores at all): must sort LAST, never as zero.
      Scored("recursive/flint-v1.0:free", coding: null, provider: "Recursive"),
      Scored("poolside/laguna-s-2.1:free", coding: null, provider: "Poolside"),
      // Zero prices but NO tool capability: the music generators.
      Capabilities(Entry("google/lyria-3-pro-preview", contextLength: 1_048_576), toolUse: false),
      Capabilities(Entry("google/lyria-3-clip-preview", contextLength: 1_048_576), toolUse: false),
      // The free ROUTER: zero prices and tool capable, but it is not a model - it picks one
      // at random per request, so nothing it reports can be attributed.
      Capabilities(Entry("openrouter/free", contextLength: 200_000) with { ProviderName = null },
          isRouter: true),
      // Unpriced: absence is unknown, never free.
      Entry("mystery/unpriced", pricing: null),
      // Priced: nothing about this is free, however good its scores are.
      Scored("paid/model", coding: 99.0, intelligence: 99.0, agentic: 99.0) with { PromptPricePerToken = 0.000001m },
  ];

  [Fact]
  public void Select_LiveShapedCatalog_PicksTheHighestCodingScore()
  {
    ModelProviderEntry? chosen = BestFreeModelSelector.Select(LiveShapedCatalog());

    Assert.NotNull(chosen);
    Assert.Equal("thinkingmachines/inkling-small:free", chosen.ModelId);
    Assert.Equal("Thinking Machines", chosen.ProviderName);
  }

  [Fact]
  public void Select_MusicGenerator_IsRejectedForMissingToolCapability()
  {
    // Both music generators advertise zero prices and a huge window, so a rule that looked
    // only at price would happily suggest a song generator as the best free model.
    ModelProviderEntry? chosen = BestFreeModelSelector.Select(LiveShapedCatalog());

    Assert.NotNull(chosen);
    Assert.NotEqual("google/lyria-3-pro-preview", chosen.ModelId);
    Assert.NotEqual("google/lyria-3-clip-preview", chosen.ModelId);
  }

  [Fact]
  public void Select_UnpricedModel_IsRejected_UnknownIsNotFree()
  {
    // The defect this issue closes: an absent price parsed as zero, so an unpriced model
    // was indistinguishable from a genuinely free one.
    IReadOnlyList<ModelProviderEntry> catalog =
    [
        Scored("mystery/unpriced", coding: 99.0) with { PromptPricePerToken = null },
        Scored("known/free", coding: 10.0),
    ];

    Assert.Equal("known/free", BestFreeModelSelector.Select(catalog)?.ModelId);
  }

  [Fact]
  public void Select_Router_IsRejected_UNLESS_ExplicitlyIncluded()
  {
    // The router is scored HIGHER than the real model, so exclusion is visible: a rule
    // that merely preferred models would pass this test only by luck of ordering.
    IReadOnlyList<ModelProviderEntry> catalog =
    [
        Capabilities(Scored("openrouter/free", coding: 99.0, contextLength: 200_000), isRouter: true),
        Scored("real/model:free", coding: 1.0),
    ];

    Assert.Equal("real/model:free", BestFreeModelSelector.Select(catalog)?.ModelId);
    Assert.Equal("openrouter/free", BestFreeModelSelector.Select(catalog,
        new BestFreeModelCriteria { IncludeRouters = true })?.ModelId);
  }

  [Fact]
  public void Select_UnmeasuredModels_SortLast_NotAsZeroScores()
  {
    // A null score is not a zero: an unmeasured model must never beat a model that WAS
    // measured and scored low.
    IReadOnlyList<ModelProviderEntry> catalog =
    [
        Scored("unmeasured/first", coding: null),
        Scored("measured/low", coding: 1.0, intelligence: 1.0, agentic: 1.0),
        Scored("unmeasured/second", coding: null, intelligence: 90.0),
    ];

    Assert.Equal("measured/low", BestFreeModelSelector.Select(catalog)?.ModelId);
  }

  [Fact]
  public void Select_TiesBreakOnTheNextScoreInOrder()
  {
    // Equal coding: intelligence decides.
    Assert.Equal("b/intelligence-high", BestFreeModelSelector.Select(
    [
        Scored("c/agentic-low", coding: 50.0, intelligence: 20.0, agentic: 1.0),
        Scored("a/agentic-high", coding: 50.0, intelligence: 20.0, agentic: 9.0),
        Scored("b/intelligence-high", coding: 50.0, intelligence: 30.0, agentic: 0.5),
    ])?.ModelId);

    // Equal coding and intelligence: agentic decides.
    Assert.Equal("a/agentic-high", BestFreeModelSelector.Select(
    [
        Scored("c/agentic-low", coding: 50.0, intelligence: 20.0, agentic: 1.0),
        Scored("a/agentic-high", coding: 50.0, intelligence: 20.0, agentic: 9.0),
    ])?.ModelId);
  }

  [Fact]
  public void Select_ContextRequirement_RejectsModelsThatAreTooSmall()
  {
    // The requirement is a caller-supplied filter, never a constant in the rule.
    IReadOnlyList<ModelProviderEntry> catalog =
    [
        Scored("small/free", coding: 99.0, contextLength: 8_192),
        Scored("large/free", coding: 1.0, contextLength: 262_144),
    ];

    Assert.Equal("small/free", BestFreeModelSelector.Select(catalog)?.ModelId);
    Assert.Equal("large/free", BestFreeModelSelector.Select(catalog,
        new BestFreeModelCriteria { MinContextLength = 100_000 })?.ModelId);
  }

  [Fact]
  public void Select_MultipleEndpointsForOneModel_YieldsThatModelOnce_ByItsWidestEndpoint()
  {
    // The catalog expands one row per provider endpoint, so the same model can appear
    // several times with different windows. Selection yields the MODEL once, represented
    // by its widest endpoint (whose ProviderName rides onto a request as a provider pin).
    IReadOnlyList<ModelProviderEntry> catalog =
    [
        Scored("multi/free", coding: 10.0, contextLength: 32_768, provider: "Narrow"),
        Scored("multi/free", coding: 10.0, contextLength: 262_144, provider: "Wide"),
    ];

    ModelProviderEntry? chosen = BestFreeModelSelector.Select(catalog);

    Assert.NotNull(chosen);
    Assert.Equal("multi/free", chosen.ModelId);
    Assert.Equal("Wide", chosen.ProviderName);
  }

  [Fact]
  public void Select_NothingQualifies_ReturnsNull()
  {
    Assert.Null(BestFreeModelSelector.Select([]));
    Assert.Null(BestFreeModelSelector.Select([Entry("paid/model", pricing: 0.000001m)]));
    Assert.Null(BestFreeModelSelector.Select([Capabilities(Entry("no-tools"), toolUse: false)]));
    Assert.Null(BestFreeModelSelector.Select([Capabilities(Entry("no-text"), textOutput: false)]));
    Assert.Null(BestFreeModelSelector.Select([Capabilities(Entry("router"), isRouter: true)]));
  }

  [Fact]
  public void Rank_ReturnsEveryQualifyingModel_BestFirst_WithoutRejectedRows()
  {
    // The live suite WALKS this order: a model the catalog calls usable can still refuse at
    // run time (measured 2026-10-26: thinkingmachines/inkling-small:free answers HTTP 403,
    // "only available on agentic harnesses"), so the caller needs the next candidate without
    // reimplementing the rule. Every rejected row stays out of the list.
    IReadOnlyList<ModelProviderEntry> ranked = BestFreeModelSelector.Rank(LiveShapedCatalog());

    // Measured first, unmeasured LAST, ties broken by model id - the same rule Select uses.
    Assert.Equal([
        "thinkingmachines/inkling-small:free",
        "thinkingmachines/inkling:free",
        "nvidia/nemotron-3-ultra-550b-a55b:free",
        "google/gemma-4-31b-it:free",
        "cohere/north-mini-code:free",
        "poolside/laguna-s-2.1:free",
        "recursive/flint-v1.0:free",
    ], ranked.Select(entry => entry.ModelId));

    // The rejected rows are absent, not merely ranked low.
    Assert.DoesNotContain(ranked, entry => entry.ModelId is "google/lyria-3-pro-preview"
        or "google/lyria-3-clip-preview" or "openrouter/free" or "mystery/unpriced" or "paid/model");

    // Rank()[0] IS Select(): one rule, two shaped answers.
    Assert.Equal(BestFreeModelSelector.Select(LiveShapedCatalog())?.ModelId, ranked[0].ModelId);
  }

  [Fact]
  public void Select_FullTie_IsDeterministic()
  {
    // Identical scores: the pick must be stable across runs, because the live suite RECORDS
    // the resolved id - a pick that flips between runs makes a recorded pin meaningless.
    IReadOnlyList<ModelProviderEntry> catalog =
    [
        Scored("zeta/model:free", coding: 5.0),
        Scored("alpha/model:free", coding: 5.0),
    ];

    Assert.Equal("alpha/model:free", BestFreeModelSelector.Select(catalog)?.ModelId);
    Assert.Equal("alpha/model:free", BestFreeModelSelector.Select([.. catalog.Reverse()])?.ModelId);
  }
}
