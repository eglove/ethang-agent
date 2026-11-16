namespace eThangAgent.ModelDomain;

/// <summary>The endpoint order a Cheapest request carries, and what that order can and cannot
///     justify: the slugs cheapest-first, the winner, the runner-up, whether the winner holds at
///     every usage shape, and - when it does not - the cache-hit rate at which the two cross.
///     <para>THE ORDER IS A PREFERENCE, NOT A PIN. The request sends <c>allow_fallbacks: true</c>,
///     so an unavailable listed endpoint falls through to the rest of the pool rather than failing
///     the turn.</para>
///     <para>WHY THE FRAGILITY CLAIM EXISTS: a winner computed at one usage shape is an estimate,
///     and an estimate presented as a fact is the defect this feature removes. When the winner
///     DOMINATES the runner-up - no more expensive on all three rates, strictly cheaper on one -
///     it is cheapest at every hit rate and every output/input ratio, so the answer needs no
///     estimate at all. When it does not, the answer depends on the shape, and
///     <see cref="CheapestRanking.IsFragile"/> says so instead of asserting a name.</para>
///     <para>PURE: no clock, no store, no network. The shape is an argument, so the caller is
///     responsible for naming the assumption and the same input list always produces the same
///     ranking.</para></summary>
public static class CheapestOrder
{
  /// <summary>How many endpoints a request names. A model can carry 37 endpoints; sending all of
  ///     them bloats every request for no benefit, because fallbacks cover the rest of the pool.
  ///     </summary>
  public const int MaxOrderedEndpoints = 8;

  /// <summary>The ranking for one model's catalog rows at <paramref name="shape"/>.
  ///     <para>A ROW WITH NO ENDPOINT TAG IS DROPPED, not ranked: the order exists to name
  ///     endpoints, and a row whose price describes no distinct endpoint cannot be named. The
  ///     filter is on the tag rather than on <see cref="ModelProviderEntry.RoutingSlug"/>, which
  ///     falls back to the provider NAME when the payload carried no tag.</para>
  ///     <para>A ROW WHOSE UNIT COST IS UNKNOWN SORTS LAST AND IS NEVER THE WINNER: no advertised
  ///     input or output price means unknown, and unknown is never cheapest.</para>
  ///     <para>THE ORDER IS DETERMINISTIC: unit cost, then input rate, then slug compared
  ///     ordinally, so two identical inputs produce an identical list in any input order - which
  ///     is required, because the list is persisted as a routing preference.</para></summary>
  /// <param name="endpoints">The catalog rows for ONE model.</param>
  /// <param name="shape">The hit rate and output/input ratio to price every row at.</param>
  public static CheapestRanking For(IReadOnlyList<ModelProviderEntry> endpoints, CostShape shape)
  {
    ArgumentNullException.ThrowIfNull(endpoints);

    List<(ModelProviderEntry Entry, decimal? Unit)> ranked = [.. endpoints
        .Where(entry => entry is not null && entry.Variant.Slug is { Length: > 0 })
        .Select(entry => (Entry: entry, Unit: CheapestCost.Unit(entry, shape)))
        .OrderBy(row => row.Unit is null ? 1 : 0)
        .ThenBy(row => row.Unit)
        .ThenBy(row => InputRate(row.Entry))
        .ThenBy(row => row.Entry.Variant.Slug, StringComparer.Ordinal)
        .Take(MaxOrderedEndpoints)];

    ModelProviderEntry? winner = Priced(ranked, 0);
    ModelProviderEntry? runnerUp = Priced(ranked, 1);

    // A single priced endpoint has nothing to be fragile against: it is the only endpoint whose
    // cost is known, so no shape-dependent choice was made.
    bool isFragile = winner is not null
        && runnerUp is not null
        && !CheapestCost.Dominates(winner, runnerUp);

    decimal? breakEven = isFragile
        ? CheapestCost.BreakEvenHitRate(winner, runnerUp, shape.OutputPerInputRatio)
        : null;

    return new CheapestRanking(
        [.. SlugsOf(ranked)],
        winner?.Variant.Slug,
        breakEven,
        runnerUp?.Variant.Slug,
        isFragile)
    {
      WinnerCheaperBelowBreakEven = breakEven is { } crossing
          && CheapestCost.Unit(winner, shape with { HitRate = Below(crossing) })
              < CheapestCost.Unit(runnerUp, shape with { HitRate = Below(crossing) }),
    };
  }

  /// <summary>A hit rate just below <paramref name="crossing"/>, clamped at zero so a crossing
  ///     at the very bottom of the range still has a reachable side to test.</summary>
  private static decimal Below(decimal crossing) => crossing > 0.01m ? crossing - 0.01m : 0m;

  /// <summary>The <paramref name="offset"/>-th endpoint in the order whose unit cost is known,
  ///     or null when the order holds fewer than that many priced endpoints. Unknown-cost rows
  ///     sort last, so this never has to skip past one to find a priced row.</summary>
  private static ModelProviderEntry? Priced(
      IReadOnlyList<(ModelProviderEntry Entry, decimal? Unit)> ranked, int offset)
  {
    List<ModelProviderEntry> priced = [.. ranked.Where(row => row.Unit is not null).Select(row => row.Entry)];
    return offset < priced.Count ? priced[offset] : null;
  }

  private static IEnumerable<string> SlugsOf(
      IReadOnlyList<(ModelProviderEntry Entry, decimal? Unit)> ranked)
      => ranked.Select(row => row.Entry.Variant.Slug!);

  /// <summary>The endpoint's advertised input rate, or <see cref="decimal.MaxValue"/> when it
  ///     advertised none - the tiebreak never ranks an unknown rate ahead of a known one.
  ///     </summary>
  private static decimal InputRate(ModelProviderEntry entry)
      => entry.PromptPricePerToken is { } price && price >= 0m ? price : decimal.MaxValue;
}

/// <summary>What a Cheapest order says, and what it can justify.
///     <para><see cref="Slugs"/> is the list a request's <c>provider.order</c> carries: cheapest
///     first, at most <see cref="CheapestOrder.MaxOrderedEndpoints"/> entries, never an empty or
///     null slug.</para>
///     <para><see cref="WinnerSlug"/> and <see cref="RunnerUpSlug"/> are the two cheapest
///     endpoints whose cost is KNOWN; either is null when the order holds fewer than two priced
///     endpoints, so a surface states that rather than naming an unpriced endpoint as cheapest.
///     </para>
///     <para><see cref="IsFragile"/> is true when the winner does not dominate the runner-up, so
///     the choice depends on the usage shape. <see cref="BreakEvenHitRate"/> is the cache-hit
///     rate in [0,1] at which the two cost the same, and is null when the winner dominates or
///     when the two never cross inside that range.</para></summary>
/// <param name="Slugs">The endpoint slugs, cheapest first.</param>
/// <param name="WinnerSlug">The cheapest priced endpoint's slug, or null when none is priced.</param>
/// <param name="BreakEvenHitRate">The hit rate at which winner and runner-up cost the same, or
///     null when the winner dominates or no crossing is reachable.</param>
/// <param name="RunnerUpSlug">The second cheapest priced endpoint's slug, or null when there is
///     no second priced endpoint.</param>
/// <param name="IsFragile">True when the winner does not dominate the runner-up.</param>
public sealed record CheapestRanking(
    IReadOnlyList<string> Slugs,
    string? WinnerSlug,
    decimal? BreakEvenHitRate,
    string? RunnerUpSlug,
    bool IsFragile)
{
  /// <summary>True when the winner is the CHEAPER of the two below
  ///     <see cref="BreakEvenHitRate"/> - so the crossing is a point above which the winner
  ///     LOSES, not one above which it starts to win.
  ///     <para>THE DIRECTION IS NOT IMPLIED BY THE CROSSING: which side of it the winner occupies
  ///     depends on which of the two discounts caching harder, and a winner that already leads at
  ///     a low hit rate is overtaken ABOVE the crossing. A surface that assumed one direction
  ///     would state the opposite of the truth on every such pair.</para>
  ///     <para>An INIT MEMBER rather than a positional one, so the record's shape is unchanged for
  ///     every caller that already constructs it.</para></summary>
  public bool WinnerCheaperBelowBreakEven { get; init; }
}
