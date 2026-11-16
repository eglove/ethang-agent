namespace eThangAgent.ModelDomain;

/// <summary>The usage shape a unit cost is computed at: the share of input tokens served from a
///     prompt cache, and how many output tokens each input token produces.
///     <para>BOTH MEMBERS ARE ARGUMENTS THE CALLER NAMES, never values read from a store or a
///     clock, so the arithmetic that consumes this shape is a pure function and a caller that
///     wants a different assumption passes a different shape.</para></summary>
/// <param name="HitRate">The share of input tokens expected to be cache hits, in [0,1].</param>
/// <param name="OutputPerInputRatio">Output tokens per input token, at or above zero.</param>
public readonly record struct CostShape(decimal HitRate, decimal OutputPerInputRatio);

/// <summary>What one endpoint costs per input token at a stated <see cref="CostShape"/>, plus the
///     two comparisons that need no shape at all.
///     <para>THE FORMULA IS LINEAR IN BOTH SHAPE NUMBERS:
///     <c>unit = (1-h)*input + h*cacheRead + r*output</c>, where h is the hit rate and r the
///     output/input ratio. The output term carries its own multiplier and is never folded into
///     an effective input price.</para>
///     <para>UNITS: every rate is a price per TOKEN, as the catalog advertises them, so
///     <see cref="Unit"/> returns a price per token. A surface showing USD per million tokens
///     multiplies by 1,000,000.</para>
///     <para>EVERY FUNCTION IS TOTAL: a null entry, an unknown price, or a pair that never
///     crosses returns null (or false) rather than throwing. An unknown price is an ordinary
///     catalog fact, and unknown never ranks as cheapest.</para>
///     <para>WHICH RATES ARE KNOWN: an absent (null) or negative advertised price is not a
///     price - the routing pseudo-models advertise -1, meaning "set at routing time" - so it
///     reads as unknown. A served ZERO is a real price of zero and is priced as one.</para>
///     <para>A MISSING CACHE-READ PRICE IS NO DISCOUNT, NOT AN UNKNOWN: the cached share is
///     charged at the input rate, which is what a provider that does not cache actually bills.
///     The same fallback covers a negative cache-read price. A consequence is that such an
///     endpoint's unit cost is constant across the hit rate.</para></summary>
public static class CheapestCost
{
  /// <summary>The total cost of one input token for one endpoint at <paramref name="shape"/>,
  ///     or null when the endpoint's input or output price is unknown.
  ///     <para>Monotonic in the hit rate: the cost strictly falls as the hit rate rises when the
  ///     cache-read rate is below the input rate, and is constant when there is no cache-read
  ///     rate (see the fallback above).</para></summary>
  /// <param name="entry">The catalog row to price, or null for an unknown cost.</param>
  /// <param name="shape">The hit rate and output/input ratio to price it at.</param>
  public static decimal? Unit(ModelProviderEntry? entry, CostShape shape)
      => RatesOf(entry) is { } rates
          ? UncachedTerm(rates, shape) + CachedTerm(rates, shape) + OutputTerm(rates, shape)
          : null;

  /// <summary>The hit rate in [0,1] at which two endpoints cost the same per input token, or
  ///     null when they never cross inside that range.
  ///     <para>SOLVED IN CLOSED FORM, because the cost is linear in the hit rate:
  ///     <c>h* = ((pb - pa) + r*(ob - oa)) / ((ca - pa) - (cb - pb))</c>. A zero denominator
  ///     means the two endpoints are parallel in the hit rate and never cross; a crossing
  ///     outside [0,1] is not reachable at any real hit rate and is reported as null too.</para>
  ///     <para>SYMMETRIC: swapping the two endpoints negates the numerator and the denominator
  ///     together, so the crossing is the same hit rate.</para>
  ///     <para>Null when either endpoint's input or output price is unknown - a pair with an
  ///     unpriced side has no crossing to solve.</para></summary>
  /// <param name="a">One endpoint.</param>
  /// <param name="b">The other endpoint.</param>
  /// <param name="outputPerInputRatio">Output tokens per input token, at or above zero.</param>
  public static decimal? BreakEvenHitRate(ModelProviderEntry? a, ModelProviderEntry? b,
      decimal outputPerInputRatio)
      => RatesOf(a) is { } left && RatesOf(b) is { } right
          ? Crossing(left, right, outputPerInputRatio)
          : null;

  /// <summary>True when <paramref name="a"/> costs no more than <paramref name="b"/> at EVERY
  ///     hit rate and EVERY output/input ratio, and strictly less at some point.
  ///     <para>THE SHAPE-FREE CLAIM: the difference between the two unit costs is a
  ///     non-negative combination of the three rate differences, so all three being no greater
  ///     with at least one strictly lower is exactly the condition for <paramref name="a"/> to
  ///     win across the whole space. No estimate is needed to prefer it.</para>
  ///     <para>IRREFLEXIVE: every difference is zero against itself, so nothing dominates
  ///     itself. False whenever either endpoint's input or output price is unknown, because a
  ///     comparison with an unpriced side cannot be established.</para></summary>
  /// <param name="a">The endpoint claimed to dominate.</param>
  /// <param name="b">The endpoint it is claimed to dominate.</param>
  public static bool Dominates(ModelProviderEntry? a, ModelProviderEntry? b)
      => RatesOf(a) is { } left && RatesOf(b) is { } right
          && left.Input <= right.Input
          && left.CacheRead <= right.CacheRead
          && left.Output <= right.Output
          && (left.Input < right.Input || left.CacheRead < right.CacheRead || left.Output < right.Output);

  private static decimal UncachedTerm(Rates rates, CostShape shape) => (1m - shape.HitRate) * rates.Input;

  private static decimal CachedTerm(Rates rates, CostShape shape) => shape.HitRate * rates.CacheRead;

  private static decimal OutputTerm(Rates rates, CostShape shape) => shape.OutputPerInputRatio * rates.Output;

  /// <summary>The closed-form crossing of two priced endpoints, or null when they are parallel in
  ///     the hit rate or cross outside [0,1].</summary>
  private static decimal? Crossing(Rates left, Rates right, decimal outputPerInputRatio)
  {
    decimal inputGap = right.Input - left.Input;
    decimal outputGap = right.Output - left.Output;
    decimal numerator = inputGap + (outputPerInputRatio * outputGap);
    decimal leftDiscount = left.CacheRead - left.Input;
    decimal rightDiscount = right.CacheRead - right.Input;
    decimal denominator = leftDiscount - rightDiscount;
    return denominator == 0m ? null : Reachable(numerator / denominator);
  }

  /// <summary>A crossing outside [0,1] is not reachable at any real hit rate, so it is no answer.</summary>
  private static decimal? Reachable(decimal crossing) => crossing is >= 0m and <= 1m ? crossing : null;

  /// <summary>The three rates an endpoint is priced at, with the cache-read fallback already
  ///     applied, or null when a rate it needs is unknown.</summary>
  private static Rates? RatesOf(ModelProviderEntry? entry)
      => entry is not null
          && Rate(entry.PromptPricePerToken) is { } input
          && Rate(entry.CompletionPricePerToken) is { } output
              ? new Rates(input, Rate(entry.PromptCacheReadPricePerToken) ?? input, output)
              : null;

  /// <summary>A negative advertised price is the routing pseudo-models' "set at routing time"
  ///     marker rather than a price; an absent one is unknown. A served zero stays a price.
  /// </summary>
  private static decimal? Rate(decimal? price) => price is { } value && value >= 0m ? value : null;

  private readonly record struct Rates(decimal Input, decimal CacheRead, decimal Output);
}
