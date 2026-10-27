using System.Globalization;

namespace eThangAgent.ModelDomain;

/// <summary>The ONE place that decides how an absent performance number is stated (issue
///     #293, D1/B5), so no surface can quietly render it as zero.
///     <para>WHY THIS EXISTS AS A CONTRACT RATHER THAN A FORMAT STRING AT EACH CALL SITE: the
///     catalog's latency and throughput fields are structurally dead - measured live on
///     2026-10-27, <c>latency_last_30m</c> and <c>throughput_last_30m</c> were null on all 52
///     endpoints across 5 models. A picker that formatted them with a plain numeric format
///     would print <c>0 ms</c> and tell the user every provider responds instantly, which is
///     the same class of lie the nullable prices exist to prevent (issue #289). The rule is
///     therefore mechanical: a null is <see cref="NotMeasured"/> and a real 0 is a real
///     measurement, and both cases are pinned by test.</para>
///     <para>Uptime IS populated on the live API (35 of 35 endpoints on one model), so its
///     absent case is rare rather than structural - but it takes the same treatment, because
///     the rule is about honesty, not about how often the gap occurs.</para></summary>
public static class ProviderPerformanceText
{
  /// <summary>What a surface says when nothing measured the number. Spelled out rather than
  ///     left blank: a blank cell reads as an oversight, and a zero reads as a measurement.</summary>
  public const string NotMeasured = "not measured";

  /// <summary>The latency cell: whole milliseconds when the catalog advertised one, else
  ///     <see cref="NotMeasured"/>. A served 0 renders as <c>0 ms</c> - a call that finished
  ///     inside a millisecond is a measurement, not an absence.</summary>
  public static string Latency(double? latencyMs)
      => latencyMs is { } value
          ? value.ToString("0", CultureInfo.InvariantCulture) + " ms"
          : NotMeasured;

  /// <summary>The throughput cell: tokens per second when the catalog advertised one, else
  ///     <see cref="NotMeasured"/>.</summary>
  public static string Throughput(double? tokensPerSecond)
      => tokensPerSecond is { } value
          ? value.ToString("0", CultureInfo.InvariantCulture) + " tok/s"
          : NotMeasured;

  /// <summary>The reliability cell: the served-request success rate as a percentage when the
  ///     catalog advertised one, else <see cref="NotMeasured"/>. One decimal, because the
  ///     difference between 99.9% and 100% is the difference between a provider that drops
  ///     requests and one that does not.</summary>
  public static string Uptime(double? uptimePercent)
      => uptimePercent is { } value
          ? value.ToString("0.#", CultureInfo.InvariantCulture) + "%"
          : NotMeasured;

  /// <summary>The cache-discount cell: the cache-read price as a fraction of the prompt price
  ///     when both are known and the prompt price is positive, else <see cref="NotMeasured"/>.
  ///     <para>A cache read that costs MORE than a fresh prompt token is a real catalog fact
  ///     (45 rows do it) and renders as a percentage above 100 rather than being clamped -
  ///     clamping would hide the one case a user most needs to see.</para></summary>
  public static string CacheDiscount(decimal? promptPrice, decimal? cacheReadPrice)
  {
    if (promptPrice is not { } prompt || cacheReadPrice is not { } cacheRead || prompt <= 0m)
    {
      return NotMeasured;
    }

    decimal discount = (1m - (cacheRead / prompt)) * 100m;
    return discount.ToString("0.#", CultureInfo.InvariantCulture) + "% off";
  }
}
