namespace eThangAgent.ModelDomain;

/// <summary>A running token/cost accumulator over usage rows (issue #24): the status
///     bar's session total, seeded from the persisted rollup at tab open and advanced by
///     each <c>usage</c> event a host emits mid-turn.
///     <para>WHY AN ACCUMULATOR AND NOT A RE-QUERY: the loop runs in the HOST process and
///     the ledger row is written there, so the app learns about a call when the event
///     arrives. Re-querying per call would be a poll, and the issue asks for
///     event-driven increments. The seed exists so a resumed or reopened session starts
///     from its real history instead of from zero.</para>
///     <para>Not thread-safe by design: the status bar applies events on its own
///     marshaled thread, one at a time.</para></summary>
public sealed class UsageTally
{
  private long _inputTokens;
  private long _outputTokens;
  private long _cachedInputTokens;
  private long _reasoningTokens;
  private decimal? _cost;
  private int _pricedCalls;
  private int _unpricedCalls;

  /// <summary>The accumulated totals.</summary>
  public UsageTotal Totals => new(_inputTokens, _outputTokens, _cachedInputTokens,
      _reasoningTokens, _cost, _pricedCalls, _unpricedCalls);

  /// <summary>Replaces the accumulator with a persisted rollup (the tab-open seed).
  ///     Called before any turn runs, so no increment can be lost to it.</summary>
  public void Seed(UsageTotal totals)
  {
    ArgumentNullException.ThrowIfNull(totals);
    _inputTokens = totals.InputTokens;
    _outputTokens = totals.OutputTokens;
    _cachedInputTokens = totals.CachedInputTokens;
    _reasoningTokens = totals.ReasoningTokens;
    _cost = totals.Cost;
    _pricedCalls = totals.PricedCalls;
    _unpricedCalls = totals.UnpricedCalls;
  }

  /// <summary>Adds one recorded call. A row that cannot be priced contributes its tokens
  ///     and counts as unpriced — the cost stays the sum of the priced rows only.</summary>
  public void Add(UsageRow row)
  {
    ArgumentNullException.ThrowIfNull(row);
    _inputTokens += row.InputTokens;
    _outputTokens += row.OutputTokens;
    _cachedInputTokens += row.CachedInputTokens ?? 0;
    _reasoningTokens += row.ReasoningTokens ?? 0;
    if (UsageCost.Of(row) is { } cost)
    {
      _cost = (_cost ?? 0m) + cost;
      _pricedCalls++;
    }
    else
    {
      _unpricedCalls++;
    }
  }
}
