using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

/// <summary>The deterministic smart-selection core: constraint filter + sort over
/// catalog entries. Pure - no LLM calls, no I/O, no time. One place holds the
/// exclusion and comparison rules so the session resolver and the Desktop preview
/// share byte-identical semantics.</summary>
public static class SmartModelSelectorCore
{
  /// <summary>The documented output-weight in the blended price: a typical agent
  /// loop emits roughly three output tokens per input token, so the price sort
  /// ranks by input + 3 x output (per-token prices, discount already applied).</summary>
  public const decimal OutputWeight = 3m;

  public static Result<ModelSelectionResult> Select(
      IReadOnlyList<ModelProviderEntry> entries,
      SmartSelectionOptions options,
      bool requireVision)
  {
    ArgumentNullException.ThrowIfNull(entries);
    ArgumentNullException.ThrowIfNull(options);

    IEnumerable<ModelProviderEntry> candidates = entries
        .Where(e => !e.ModelId.EndsWith(":batch", StringComparison.Ordinal))
        .Where(e => !e.ModelId.EndsWith(":free", StringComparison.Ordinal))
        .Where(e => !e.ModelId.StartsWith("openrouter/", StringComparison.Ordinal))
        .Where(e => e.SupportsToolUse)
        .Where(e => !requireVision || e.SupportsVision);

    if (options.MaxInputPerM is { } maxIn)
    {
      decimal cap = maxIn / 1_000_000m;
      candidates = candidates.Where(e => e.PromptPricePerToken <= cap);
    }

    if (options.MaxOutputPerM is { } maxOut)
    {
      decimal cap = maxOut / 1_000_000m;
      candidates = candidates.Where(e => e.CompletionPricePerToken <= cap);
    }

    // Strict missing-data semantics: a set floor fails a null score - an unknown
    // score cannot meet a known floor.
    if (options.MinIntelligence is { } minI)
    {
      candidates = candidates.Where(e => e.IntelligenceScore is { } v && v >= minI);
    }

    if (options.MinCoding is { } minC)
    {
      candidates = candidates.Where(e => e.CodingScore is { } v && v >= minC);
    }

    if (options.MinAgentic is { } minA)
    {
      candidates = candidates.Where(e => e.AgenticScore is { } v && v >= minA);
    }

    // Strict zero-context semantics: a set floor fails a missing window.
    if (options.MinContextWindow is { } minCtx)
    {
      candidates = candidates.Where(e => e.ContextLength > 0 && e.ContextLength >= minCtx);
    }

    List<ModelProviderEntry> qualifiers = [.. candidates];
    if (qualifiers.Count == 0)
    {
      return Result.Failure<ModelSelectionResult>(new DomainError(
          "NoMatchingModels", "No models match the smart selection constraints."));
    }

    // One ordered pass per sort criterion; the tie-break chain (blended price for
    // score sorts, then model-id ordinal everywhere) makes the pick independent of
    // catalog order. Null scores rank last (double.NegativeInfinity).
    IOrderedEnumerable<ModelProviderEntry> ordered = options.Sort switch
    {
      SmartSortCriterion.Price => qualifiers
          .OrderBy(BlendedPrice)
          .ThenBy(e => e.ModelId, StringComparer.Ordinal),
      SmartSortCriterion.Intelligence => OrderedByScore(qualifiers, e => e.IntelligenceScore),
      SmartSortCriterion.Coding => OrderedByScore(qualifiers, e => e.CodingScore),
      SmartSortCriterion.Agentic => OrderedByScore(qualifiers, e => e.AgenticScore),
      _ => throw new InvalidOperationException($"Unknown sort criterion {options.Sort}."),
    };

    ModelProviderEntry best = ordered.First();
    int n = qualifiers.Count;
    string reasoning = options.Sort switch
    {
      SmartSortCriterion.Price => $"cheapest of {n} qualifying models",
      SmartSortCriterion.Intelligence => $"Intelligence of {n} qualifying models",
      SmartSortCriterion.Coding => $"Coding of {n} qualifying models",
      SmartSortCriterion.Agentic => $"Agentic of {n} qualifying models",
      _ => throw new InvalidOperationException($"Unknown sort criterion {options.Sort}."),
    };
    return Result.Success(new ModelSelectionResult(best.ModelId, best.ProviderName, reasoning));
  }

  private static decimal BlendedPrice(ModelProviderEntry e) => e.PromptPricePerToken + (OutputWeight * e.CompletionPricePerToken);

  private static IOrderedEnumerable<ModelProviderEntry> OrderedByScore(
      IReadOnlyList<ModelProviderEntry> qualifiers, Func<ModelProviderEntry, double?> score)
      => qualifiers.OrderByDescending(e => score(e) ?? double.NegativeInfinity)
          .ThenBy(BlendedPrice)
          .ThenBy(e => e.ModelId, StringComparer.Ordinal);
}

/// <summary>The IModelSelector face of smart selection: fetches the catalog and
/// delegates to the pure core. Options arrive per call - the session resolver
/// passes the carrier's snapshot, the Desktop preview its live draft.</summary>
public sealed class SmartModelSelector(IModelCatalog catalog) : IModelSelector
{
  private readonly IModelCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

  public async Task<Result<ModelSelectionResult>> SelectAsync(
      SmartSelectionOptions options, bool requireVision, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(options);
    Result<IReadOnlyList<ModelProviderEntry>> entries = await _catalog.GetAsync(ct).ConfigureAwait(false);
    return !entries.IsSuccess
        ? Result.Failure<ModelSelectionResult>(entries.Error)
        : SmartModelSelectorCore.Select(entries.Value, options, requireVision);
  }
}
