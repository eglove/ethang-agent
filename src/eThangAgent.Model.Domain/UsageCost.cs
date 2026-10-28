namespace eThangAgent.ModelDomain;

/// <summary>Returns a provider-reported charge when available. New rows with no charge are
///     unpriced; legacy rows use the estimate below and are labeled by the tally. The estimate
///     applies the prices a row stamped at write time:
///     <c>uncached_input x prompt_price + cached_input x cache_read_price
///     + output x completion_price</c>.
///     <para>WHY CACHED TOKENS GET THEIR OWN PRICE: the catalog publishes a cache-read
///     price for most models (median 0.1x the prompt price), and this harness runs at a
///     ~98% cache-hit rate, so charging every input token at the prompt price overstated
///     real spend by ~8x at the median ratio. The former "conservative overestimate" rule
///     is dropped.</para>
///     <para>A row is PRICED only when every price its own counts need is present. A
///     partial price would be an understatement presented as a fact, which is the same
///     lie as an overstatement: a row whose cached share cannot be priced is UNPRICED —
///     it contributes tokens and no cost, and the summary counts it as such. A null price
///     is never read as zero.</para></summary>
public static class UsageCost
{
  /// <summary>The row's cost, or null when any price its counts require is unknown.</summary>
  public static decimal? Of(UsageRow row)
  {
    ArgumentNullException.ThrowIfNull(row);
    if (row.ReportedCost is >= 0m)
    {
      return row.ReportedCost;
    }

    if (row.ExpectsReportedCost == true)
    {
      return null;
    }
    // A provider's input count INCLUDES its cached share, so the uncached share is the
    // difference. A report claiming more cached than input tokens is nonsense; clamping
    // to zero keeps the arithmetic defined without inventing a negative charge.
    int cached = Math.Max(0, row.CachedInputTokens ?? 0);
    int uncached = Math.Max(0, row.InputTokens - cached);
    decimal cost = 0m;
    if (uncached > 0)
    {
      if (row.PromptPricePerToken is not { } prompt)
      {
        return null;
      }

      cost += uncached * prompt;
    }

    if (cached > 0)
    {
      if (row.PromptCacheReadPricePerToken is not { } cacheRead)
      {
        return null;
      }

      cost += cached * cacheRead;
    }

    if (row.OutputTokens > 0)
    {
      if (row.CompletionPricePerToken is not { } completion)
      {
        return null;
      }

      cost += row.OutputTokens * completion;
    }

    return cost;
  }
}
