namespace eThangAgent.ModelDomain;

/// <summary>What a caller requires of a "best free model" pick. Every knob is an explicit
///     choice at the CALL SITE rather than a constant inside the rule: a caller sending a
///     real harness prompt needs a window big enough for it, while a future model-picker
///     option would take the default (no minimum).
///     <para>NO PRODUCTION CONSUMER TODAY: the live-model suite (issue #289) was this rule's
///     only caller and was deleted on 2026-10-27. It is kept because it is a pure, tested
///     rule that a future "best free" picker option would use unchanged - see issue #296's
///     follow-up note. Deleting it is a product decision, not a cleanup.</para></summary>
public sealed record BestFreeModelCriteria
{
  /// <summary>The smallest context window that is still useful, in tokens. Zero means no
  ///     minimum at all - the default, because the rule itself knows nothing about what a
  ///     caller intends to send.</summary>
  public int MinContextLength { get; init; }

  /// <summary>Whether to consider router pseudo-models. OFF by default, and that default is
  ///     load-bearing: a router advertises zero prices and tool support, but it picks a model
  ///     AT RANDOM per request, so nothing a run reports through it can be attributed to a
  ///     model. A caller that genuinely wants a router says so.</summary>
  public bool IncludeRouters { get; init; }

  /// <summary>Whether the pick must advertise tool support. On by default: a model that
  ///     cannot call a tool cannot drive this harness.</summary>
  public bool RequireToolUse { get; init; } = true;

  /// <summary>Whether the pick must produce text. On by default: a text-to-audio
  ///     generator advertises zero prices too, and is useless here.</summary>
  public bool RequireTextOutput { get; init; } = true;
}

/// <summary>Picks the best FREE model from a provider catalog (issue #289), as a PURE
///     function over <see cref="ModelProviderEntry"/> rows - no network, no clock, no
///     state - so it is unit tested against a fixture catalog and behaves identically for
///     every caller.
///     <para>WHAT COUNTS AS FREE: both billable prices are PRESENT and zero. An absent price
///     is UNKNOWN, never free - the catalog defect this issue closes, where a missing
///     pricing block parsed as zero and an unpriced model was indistinguishable from a free
///     one.</para>
///     <para>HOW PICKS RANK: coding score, then intelligence, then agentic, each descending,
///     with a NULL SCORE ALWAYS LAST - an unmeasured model is not a zero-scoring one, so it
///     must never beat a model that was measured and scored low. Ties fall to the model id,
///     so the same catalog always yields the same pick: a caller that RECORDS the resolved
///     id (the live suite does) gets a stable name, not a name that flips between runs.</para>
///     <para>The pick is returned as a single <see cref="ModelProviderEntry"/> - its
///     ProviderName rides onto a request as a provider pin, so the representative endpoint
///     is the WIDEST qualifying one (ties fall to the provider name) rather than an
///     arbitrary member of the group.</para></summary>
public static class BestFreeModelSelector
{
  /// <summary>The best free model in <paramref name="entries"/>, or null when nothing
  ///     qualifies. Null is an ANSWER, not a failure: a catalog with no free model has no
  ///     best free model, and the caller reports that rather than substituting one.</summary>
  public static ModelProviderEntry? Select(IReadOnlyList<ModelProviderEntry> entries,
      BestFreeModelCriteria? criteria = null)
  {
    IReadOnlyList<ModelProviderEntry> ranked = Rank(entries, criteria);
    return ranked.Count == 0 ? null : ranked[0];
  }

  /// <summary>The qualifying models, BEST FIRST - one representative entry per model id.
  ///     <para>WHY A WHOLE ORDER AND NOT JUST THE WINNER: the catalog cannot say whether a
  ///     model will actually serve. Measured 2026-10-26: the best-scoring free model,
  ///     <c>thinkingmachines/inkling-small:free</c>, answers every request with HTTP 403 -
  ///     "only available on agentic harnesses" - so a caller that took only the first pick
  ///     had no model at all. Everything else about the row (free, tool-capable, text,
  ///     1M-token window) was accurate; the refusal lives outside the catalog entirely. A
  ///     caller that can discover such a refusal at RUN TIME (the live suite, and any future
  ///     picker offering a "best free" option) walks this order instead of reimplementing
  ///     the rule.</para></summary>
  public static IReadOnlyList<ModelProviderEntry> Rank(IReadOnlyList<ModelProviderEntry> entries,
      BestFreeModelCriteria? criteria = null)
  {
    ArgumentNullException.ThrowIfNull(entries);
    BestFreeModelCriteria rules = criteria ?? new BestFreeModelCriteria();

    return [.. entries
        .Where(entry => Qualifies(entry, rules))
        // One row per MODEL: the catalog expands one row per provider endpoint, and
        // selection yields a model, represented by its widest qualifying endpoint.
        .GroupBy(entry => entry.ModelId, StringComparer.Ordinal)
        .Select(Widest)
        .OrderByDescending(entry => entry.CodingScore.HasValue)
        .ThenByDescending(entry => entry.CodingScore)
        .ThenByDescending(entry => entry.IntelligenceScore.HasValue)
        .ThenByDescending(entry => entry.IntelligenceScore)
        .ThenByDescending(entry => entry.AgenticScore.HasValue)
        .ThenByDescending(entry => entry.AgenticScore)
        .ThenBy(entry => entry.ModelId, StringComparer.Ordinal)];
  }

  /// <summary>Every condition a row must meet to be considered at all. Each test in the
  ///     suite names one of these with a live-shaped row that would otherwise win.</summary>
  private static bool Qualifies(ModelProviderEntry entry, BestFreeModelCriteria rules)
      => entry.IsAdvertisedFree
          && entry.ContextLength >= rules.MinContextLength
          && (!rules.RequireToolUse || entry.SupportsToolUse)
          && (!rules.RequireTextOutput || entry.SupportsTextOutput)
          && (rules.IncludeRouters || !entry.IsRouter);

  /// <summary>The group's widest qualifying endpoint: ties fall to the provider name so the
  ///     choice is deterministic. A provider-less endpoint sorts last - a null never pins.</summary>
  private static ModelProviderEntry Widest(IEnumerable<ModelProviderEntry> group)
      => group
          .OrderByDescending(entry => entry.ContextLength)
          .ThenBy(entry => entry.ProviderName ?? "\uFFFF", StringComparer.Ordinal)
          .First();
}
