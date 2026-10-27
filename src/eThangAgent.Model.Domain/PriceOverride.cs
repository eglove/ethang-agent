namespace eThangAgent.ModelDomain;

/// <summary>One tier of an endpoint's price ladder (issue #293): the prices that apply once a
///     call's prompt reaches <see cref="MinPromptTokens"/>. The endpoints API serves these
///     under <c>pricing.overrides</c>, and the SHAPE IS A PROMPT-SIZE TIER, not a time window.
///     <para>MEASURED, 2026-10-27, against the live endpoints payload: every override entry
///     carries <c>min_prompt_tokens</c> (200000 on Claude Sonnet 4.5, 272000 on GPT-6.1 Terra)
///     and a full price set; no entry carries a time bound. The parent issue (#291 D4)
///     hypothesised a peak/off-peak split because a provider appeared to serve two prices for
///     the same model - the two prices are the two TIERS of one ladder, and a long-context
///     call pays the upper one. Implementing a clock would have priced every call from the
///     wrong rung.</para>
///     <para>WHY THIS IS A CORRECTNESS BUG AND NOT A NICETY: a 300K-token call on Claude
///     Sonnet 4.5 is billed at $6/M input, but the base price is $3/M, so every long-context
///     call was stamped at HALF what it cost. The ledger's whole purpose is to state what a
///     call cost; a price ladder ignored at stamp time makes the ledger confidently wrong
///     exactly where spend is largest.</para>
///     <para>Every price is nullable on the same rule the base prices follow (issue #289): a
///     tier that omits a price leaves that price UNKNOWN rather than inheriting silently, so a
///     consumer can tell 'the tier says nothing about cache reads' from 'the tier says cache
///     reads are free'.</para></summary>
/// <param name="MinPromptTokens">The prompt size at which this tier takes over. A call whose
///     prompt is at or above it is priced from this tier.</param>
/// <param name="PromptPricePerToken">The uncached-input price in this tier, or null.</param>
/// <param name="PromptCacheReadPricePerToken">The cache-read price in this tier, or null.</param>
/// <param name="PromptCacheWritePricePerToken">The cache-write price in this tier, or null.</param>
/// <param name="CompletionPricePerToken">The output price in this tier, or null.</param>
public sealed record PriceOverride(
    int MinPromptTokens,
    decimal? PromptPricePerToken,
    decimal? PromptCacheReadPricePerToken,
    decimal? PromptCacheWritePricePerToken,
    decimal? CompletionPricePerToken)
{
  /// <summary>True when a call with <paramref name="promptTokens"/> prompt tokens is priced
  ///     from this tier.</summary>
  public bool AppliesTo(int promptTokens) => promptTokens >= MinPromptTokens;
}

/// <summary>Selects the tier a call is priced from (issue #293), as a PURE function over the
///     entry's ladder - no clock, no state.
///     <para>THE HIGHEST APPLICABLE TIER WINS, not the first: the API serves the ladder in
///     ascending order today, but a consumer that trusted the array's order would silently
///     mis-price the day a provider emitted it differently. Ordering by
///     <see cref="PriceOverride.MinPromptTokens"/> makes the answer independent of how the
///     payload happens to be arranged.</para>
///     <para>No applicable tier means the BASE prices apply, which is the honest reading: a
///     1,000-token call on a 200,000-token ladder is priced from the base rung.</para></summary>
public static class PriceTiers
{
  /// <summary>The tier <paramref name="promptTokens"/> falls in, or null when the base prices
  ///     apply (no ladder, or the prompt is below every rung).</summary>
  public static PriceOverride? Applicable(IReadOnlyList<PriceOverride>? overrides, int promptTokens)
  {
    if (overrides is null || overrides.Count == 0)
    {
      return null;
    }

    PriceOverride? best = null;
    foreach (PriceOverride candidate in overrides)
    {
      if (candidate.AppliesTo(promptTokens) && (best is null || candidate.MinPromptTokens > best.MinPromptTokens))
      {
        best = candidate;
      }
    }

    return best;
  }
}
