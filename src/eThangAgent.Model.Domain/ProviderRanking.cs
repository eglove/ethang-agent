namespace eThangAgent.ModelDomain;

/// <summary>One dimension a provider comparison can order by (issue #294, D4). Each is a
///     SEPARATE currency, never a term in a composite score: a user who cares about latency is
///     not served by a formula that trades it for dollars at a ratio this domain invented.
///     <para>The capability dimensions are split rather than merged for the same reason -
///     'intelligence' and 'coding' are different measurements, and averaging them would be the
///     composite score D4 forbids wearing a friendlier name.</para></summary>
public enum ProviderDimension
{
  /// <summary>The blended per-input-token cost at the criteria's stated hit rate
  ///     (<see cref="EffectivePrice"/>). Cheapest first is the useful order, so this is the
  ///     one dimension a caller usually leaves ascending.</summary>
  EffectiveInputPrice,

  /// <summary>How long a call takes, in milliseconds. Prefers an OBSERVED measurement and
  ///     falls back to the catalog's advertised figure, which is structurally dead (issue
  ///     #293: null on all 52 endpoints measured live) and therefore usually sorts last.</summary>
  Latency,

  /// <summary>Tokens per second, preferring an observed measurement for the same reason as
  ///     <see cref="Latency"/>.</summary>
  Throughput,

  /// <summary>The endpoint's served-request success rate as the catalog reports it (0-100).
  ///     The reliability signal that is actually populated.</summary>
  Reliability,

  /// <summary>The catalog's intelligence score.</summary>
  Intelligence,

  /// <summary>The catalog's coding score.</summary>
  Coding,

  /// <summary>The catalog's agentic score.</summary>
  Agentic,
}

/// <summary>One entry in a caller's priority order: which dimension, and which direction
///     (issue #294, D4). <see cref="Descending"/> is a DIRECTION, not a weight - it says
///     'more is better' rather than 'this matters 2.5x more', and the sort stays lexicographic
///     so the first priority decides on its own before the second is consulted at all.
///     <para>Nulls sort last in BOTH directions (D5): an unmeasured latency is not a fast
///     latency, and a descending sort that floated nulls to the top would rank the provider
///     nobody measured as the fastest one.</para></summary>
public sealed record ProviderPriority(ProviderDimension Dimension, bool Descending = false)
{
  /// <summary>This dimension, cheapest/lowest first.</summary>
  public static ProviderPriority Ascending(ProviderDimension dimension) => new(dimension);

  /// <summary>This dimension, highest first.</summary>
  public static ProviderPriority DescendingOrder(ProviderDimension dimension) => new(dimension, true);
}

/// <summary>The requirements a provider must MEET to appear in a comparison at all (issue
///     #294, D6). Kept apart from <see cref="ProviderPriority"/> on purpose: 'must have at
///     least a 1M context window' is a filter, not a preference, and mixing the two produces a
///     list whose top row violates a stated requirement.
///     <para>AN UNKNOWN VALUE FAILS A FILTER. A provider whose effective price could not be
///     computed cannot be shown to cost at most the caller's maximum, and one the catalog
///     never measured cannot be shown to be reliable enough - so both are excluded rather than
///     ranked low. The alternative would present a row that may violate the requirement as if
///     it satisfied it.</para></summary>
/// <param name="MaxEffectiveInputPricePerToken">The most a blended input token may cost, or
///     null for no price ceiling.</param>
/// <param name="MinUptimePercent">The least served-request success rate the provider may
///     report, or null for no reliability floor.</param>
/// <param name="MinContextLength">The smallest context window that is still usable, in
///     tokens. Zero means no minimum.</param>
public sealed record ProviderFilters(
    decimal? MaxEffectiveInputPricePerToken = null,
    double? MinUptimePercent = null,
    int MinContextLength = 0)
{
  /// <summary>No requirements at all: every row is compared.</summary>
  public static ProviderFilters None { get; } = new();
}

/// <summary>Everything a comparison needs from its caller (issue #294, D1/D4/D6): the priority
///     order, the hit rate the effective prices are computed at, the prompt size that selects a
///     price-ladder rung, and the hard filters.
///     <para>THE HIT RATE IS REQUIRED AND HAS NO DEFAULT. A default would be a hit rate this
///     domain invented, and the whole point of D1 is that the caller decides which rate
///     applies and is responsible for naming it.</para></summary>
/// <param name="Priorities">The dimensions in priority order. Empty means 'no stated
///     preference', which still yields a deterministic order (by model, then provider).</param>
/// <param name="HitRate">The share of input tokens expected to be cache hits, in [0,1].</param>
/// <param name="PromptTokens">The prompt size the comparison is for, which selects the
///     applicable price-ladder rung (issue #293). Zero selects the base prices.</param>
/// <param name="Filters">The hard requirements, or null for none.</param>
public sealed record RankingCriteria(
    IReadOnlyList<ProviderPriority> Priorities,
    decimal HitRate,
    int PromptTokens = 0,
    ProviderFilters? Filters = null)
{
  /// <summary>The hard requirements, never null.</summary>
  public ProviderFilters Requirements => Filters ?? ProviderFilters.None;
}

/// <summary>One catalog row paired with the measurements a caller OBSERVED for it (issue
///     #294), which ranking prefers over the catalog's own advertised figures.
///     <para>WHY OBSERVATIONS ARE A SEPARATE INPUT: the catalog's latency and throughput fields
///     are structurally dead (issue #293 measured them null on all 52 endpoints across 5
///     models), so a comparison that could only read the catalog would rank every provider
///     identically on latency. The observed values come from the usage ledger, which the read
///     model (issue #295) supplies; this domain only needs the numbers, never the store.</para>
///     <para>An observation is null when nothing measured it - NOT zero, which would rank a
///     provider nobody measured as instant.</para></summary>
/// <param name="Entry">The catalog row being compared.</param>
/// <param name="ObservedLatencyMs">Our own measured call duration, or null when unmeasured.</param>
/// <param name="ObservedThroughputTokensPerSec">Our own measured throughput, or null when
///     unmeasured.</param>
public sealed record RankableProvider(
    ModelProviderEntry Entry,
    double? ObservedLatencyMs = null,
    double? ObservedThroughputTokensPerSec = null)
{
  /// <summary>A row with no observations, so the catalog's own figures apply.</summary>
  public static RankableProvider Of(ModelProviderEntry entry)
      => new(entry ?? throw new ArgumentNullException(nameof(entry)));
}

/// <summary>One ranked row (issue #294): the catalog entry, the effective price it was ranked
///     on, and the observations that were available for it.
///     <para><see cref="EffectivePrice"/> rides the result so no caller recomputes it - a
///     second copy of the formula is exactly what <see cref="UsageCost"/>'s precedent exists to
///     prevent, and a caller that recomputed it at a different hit rate would display a price
///     the order was not based on.</para></summary>
public sealed record RankedProvider(
    ModelProviderEntry Entry,
    EffectiveInputPrice EffectivePrice,
    double? ObservedLatencyMs = null,
    double? ObservedThroughputTokensPerSec = null)
{
  /// <summary>The latency a surface shows: our measurement when one exists, else the catalog's
  ///     advertised figure (usually null - see <see cref="ProviderDimension.Latency"/>).</summary>
  public double? LatencyMs => ObservedLatencyMs ?? Entry.LatencyMs;

  /// <summary>The throughput a surface shows, on the same rule as <see cref="LatencyMs"/>.</summary>
  public double? ThroughputTokensPerSec => ObservedThroughputTokensPerSec ?? Entry.ThroughputTokensPerSec;
}

/// <summary>Orders provider rows by a caller's stated priorities (issue #294, D4), as a PURE
///     function over catalog rows - no clock, no store, no UI, so it is unit tested against a
///     fixture catalog and behaves identically for every caller.
///     <para>THE SORT IS LEXICOGRAPHIC, NOT COMPOSITE: the first priority decides on its own,
///     and later priorities are consulted only among rows the earlier ones tied. No weights are
///     invented anywhere here - a caller that wants weights supplies them explicitly as a
///     normalization of its own, and the picker's presets (issue #296) are that caller.</para>
///     <para>HARD FILTERS RUN FIRST (D6): a row that fails a requirement is EXCLUDED, never
///     ranked low, because a list whose top row violates a stated requirement is worse than a
///     shorter list.</para>
///     <para>TIES FALL TO THE MODEL ID, THEN THE PROVIDER NAME, so the same catalog and the
///     same priorities always yield the same order - a comparison that reshuffled equal rows
///     between renders would look like it was reacting to something.</para></summary>
public static class ProviderRanking
{
  /// <summary>The qualifying rows, best first. Rows failing a hard filter are absent.
  ///     <para>An empty result is an ANSWER: nothing met the requirements, and the caller says
  ///     so rather than relaxing a filter the user set.</para></summary>
  public static IReadOnlyList<RankedProvider> Rank(IReadOnlyList<ModelProviderEntry> entries,
      RankingCriteria criteria)
  {
    ArgumentNullException.ThrowIfNull(entries);
    ArgumentNullException.ThrowIfNull(criteria);
    return Rank([.. entries.Select(RankableProvider.Of)], criteria);
  }

  /// <summary>The qualifying rows, best first, ranking on the caller's observations where it
  ///     supplied any.</summary>
  public static IReadOnlyList<RankedProvider> Rank(IReadOnlyList<RankableProvider> providers,
      RankingCriteria criteria)
  {
    ArgumentNullException.ThrowIfNull(providers);
    ArgumentNullException.ThrowIfNull(criteria);
    List<RankedProvider> ranked = [];
    foreach (RankableProvider provider in providers)
    {
      ArgumentNullException.ThrowIfNull(provider);
      EffectiveInputPrice price = EffectivePrice.For(provider.Entry, criteria.HitRate,
          criteria.PromptTokens);
      if (!Passes(provider.Entry, price, criteria.Requirements))
      {
        continue;
      }

      ranked.Add(new RankedProvider(provider.Entry, price, provider.ObservedLatencyMs,
          provider.ObservedThroughputTokensPerSec));
    }

    ranked.Sort((left, right) => Compare(left, right, criteria.Priorities));
    return ranked;
  }

  /// <summary>Whether a row meets every hard requirement (D6). An UNKNOWN value fails: a
  ///     provider whose price could not be computed cannot be shown to be within a ceiling.
  ///     </summary>
  private static bool Passes(ModelProviderEntry entry, EffectiveInputPrice price,
      ProviderFilters filters)
      => entry.ContextLength >= filters.MinContextLength
          && (filters.MaxEffectiveInputPricePerToken is not { } maxPrice
              || (price.ReadPerInputToken is { } cost && cost <= maxPrice))
          && (filters.MinUptimePercent is not { } minUptime
              || (entry.UptimeLast30m is { } uptime && uptime >= minUptime));

  /// <summary>Lexicographic comparison over the caller's priorities, then the deterministic
  ///     tiebreak. The first non-zero answer decides.</summary>
  private static int Compare(RankedProvider left, RankedProvider right,
      IReadOnlyList<ProviderPriority> priorities)
  {
    foreach (ProviderPriority priority in priorities)
    {
      int order = CompareDimension(left, right, priority);
      if (order != 0)
      {
        return order;
      }
    }

    int byModel = string.CompareOrdinal(left.Entry.ModelId, right.Entry.ModelId);
    return byModel != 0
        ? byModel
        : string.CompareOrdinal(left.Entry.ProviderName ?? "\uFFFF",
            right.Entry.ProviderName ?? "\uFFFF");
  }

  /// <summary>One dimension's contribution to the order. NULLS ARE PLACED BEFORE THE DIRECTION
  ///     IS APPLIED (D5), which is what makes an unmeasured value sort last in both ascending
  ///     and descending order - flipping the whole comparison would float it to the top of a
  ///     descending sort and rank the provider nobody measured as the best one.</summary>
  private static int CompareDimension(RankedProvider left, RankedProvider right,
      ProviderPriority priority)
  {
    decimal? leftValue = Value(left, priority.Dimension);
    decimal? rightValue = Value(right, priority.Dimension);
    if (leftValue is null && rightValue is null)
    {
      return 0;
    }

    if (leftValue is null)
    {
      return 1;
    }

    if (rightValue is null)
    {
      return -1;
    }

    int order = leftValue.Value.CompareTo(rightValue.Value);
    return priority.Descending ? -order : order;
  }

  /// <summary>The comparable value of one dimension, or null when nothing measured it. Every
  ///     dimension yields a single number so the sort stays lexicographic; the capability
  ///     dimensions stay SEPARATE members rather than being combined here.</summary>
  private static decimal? Value(RankedProvider provider, ProviderDimension dimension)
      => dimension switch
      {
        ProviderDimension.EffectiveInputPrice => provider.EffectivePrice.ReadPerInputToken,
        ProviderDimension.Latency => ToDecimal(provider.LatencyMs),
        ProviderDimension.Throughput => ToDecimal(provider.ThroughputTokensPerSec),
        ProviderDimension.Reliability => ToDecimal(provider.Entry.UptimeLast30m),
        ProviderDimension.Intelligence => ToDecimal(provider.Entry.IntelligenceScore),
        ProviderDimension.Coding => ToDecimal(provider.Entry.CodingScore),
        ProviderDimension.Agentic => ToDecimal(provider.Entry.AgenticScore),
        _ => null,
      };

  /// <summary>A double measurement as the sort's comparable value. A catalog score or an
  ///     observed duration is never large enough for the decimal conversion to lose anything
  ///     that matters, and keeping one comparable type keeps the comparison in one place.
  ///     </summary>
  private static decimal? ToDecimal(double? value) => value is { } number ? (decimal)number : null;
}
