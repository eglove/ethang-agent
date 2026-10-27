namespace eThangAgent.ModelDomain;

/// <summary>The cache-hit rate each catalog row's price is projected at, resolved per
///     model+provider pair (issue #296 follow-up).
///     <para>WHY A LOOKUP AND NOT ONE RATE: measured live 2026-10-27, the seven endpoints
///     serving one model ranged from 0% to 74.5% cache-hit rate. A single rate for the whole
///     list is therefore wrong for nearly every row on it, and the row it is most wrong about
///     is the one the user is deciding between - a provider that caches heavily would be
///     ranked as though it never cached.</para>
///     <para>THE FALLBACK RATE IS PART OF THE LOOKUP, so a caller can never hold a per-pair
///     rate without also holding what applies to a pair nobody resolved. That is what stops a
///     missing key from silently becoming an implied zero.</para>
///     <para>IMMUTABLE AND KEYED BY THE PAIR: the key is
///     <see cref="ModelProviderEntry.Key"/>, the same composite the ranking and the
///     performance report use, so a rate can never bind to a different provider's row.</para>
///     </summary>
public sealed class HitRateLookup
{
  private readonly IReadOnlyDictionary<string, HitRateAssumption> _byPair;

  private HitRateLookup(IReadOnlyDictionary<string, HitRateAssumption> byPair,
      HitRateAssumption fallback)
  {
    _byPair = byPair;
    Fallback = fallback;
  }

  /// <summary>The rate for a pair nobody resolved. Always present, so a surface can state
  ///     what a row is projected at even when no evidence exists for it.</summary>
  public HitRateAssumption Fallback { get; }

  /// <summary>A lookup with no per-pair evidence: every row uses the fallback.</summary>
  public static HitRateLookup Uniform(HitRateAssumption fallback)
      => new(new Dictionary<string, HitRateAssumption>(StringComparer.Ordinal),
          fallback ?? throw new ArgumentNullException(nameof(fallback)));

  /// <summary>The rate for one row: its resolved rate when one exists, else the fallback.
  ///     NEVER null and never an implied zero - a row with no evidence is projected at the
  ///     documented default and says so.</summary>
  public HitRateAssumption For(ModelProviderEntry entry)
  {
    ArgumentNullException.ThrowIfNull(entry);
    return _byPair.TryGetValue(entry.Key, out HitRateAssumption? rate) ? rate : Fallback;
  }

  /// <summary>Builds a lookup from resolved pairs, keeping the fallback for everything else.
  ///     <para>THE LOOKUP IS TOTAL OVER WHATEVER THE CATALOG HANDS IT, and that includes
  ///     REPEATED PAIR KEYS. OpenRouter serves several endpoints under one provider name -
  ///     region and ZDR variants of the same vendor - so the catalog legitimately emits
  ///     several rows whose <see cref="ModelProviderEntry.Key"/> is identical (measured
  ///     2026-10-27: 1509 catalog rows over 1170 distinct pairs, so 339 rows share a key with
  ///     another). <c>ToDictionary</c> throws <c>An item with the same key has already been
  ///     added</c> on the second one, and because this runs while the picker loads, the whole
  ///     Model Settings dialog died on a raw exception instead of a model list.</para>
  ///     <para>A DUPLICATE LOSES NOTHING: the rate is keyed by the PAIR, so every row sharing
  ///     a key resolves the same rate by construction - the resolution reads only the model id,
  ///     the provider name, and the key. The FIRST entry wins, which keeps the result
  ///     deterministic for a given input order rather than dependent on dictionary internals.
  ///     </para></summary>
  public static HitRateLookup Create(IEnumerable<KeyValuePair<string, HitRateAssumption>> rates,
      HitRateAssumption fallback)
  {
    ArgumentNullException.ThrowIfNull(rates);
    ArgumentNullException.ThrowIfNull(fallback);
    Dictionary<string, HitRateAssumption> map = new(StringComparer.Ordinal);
    foreach (KeyValuePair<string, HitRateAssumption> pair in rates)
    {
      // A null key or value is a caller's bug, not a rate: dropping it keeps the lookup total
      // (every row still resolves through the fallback) instead of failing a whole comparison
      // over one malformed entry. A repeated key is absorbed the same way - the first rate for
      // a pair stands, because the pair is the whole key.
      if (pair.Key is null || pair.Value is null)
      {
        continue;
      }

      _ = map.TryAdd(pair.Key, pair.Value);
    }

    return new HitRateLookup(map, fallback);
  }
}

/// <summary>Resolves the cache-hit rate for each catalog row from the evidence available
///     (issue #296 follow-up), as a PURE function so the picker cannot invent a second
///     fallback order that disagrees with the one the domain documents.
///     <para>THE CHAIN IS FIXED AND DOCUMENTED, strongest evidence first:</para>
///     <list type="number">
///     <item>our own ledger's measurement for this exact pair - the only rate that describes
///     this session's prompt shapes;</item>
///     <item>the rate the provider publishes for this endpoint - measured across OpenRouter's
///     traffic, so real evidence about the pair even when we have never called it;</item>
///     <item>the documented default, named as a default.</item>
///     </list>
///     <para>WHY PUBLISHED BEATS DEFAULT: the user's case is a pair they have never used.
///     There is no measurement to make, but the provider has published one, and a published
///     figure for that endpoint is strictly better evidence than a constant. It is not OUR
///     measurement, so <see cref="HitRateSource.Published"/> keeps the distinction visible
///     rather than laundering it into a measured-looking number.</para>
///     <para>A PAIR WITH NEITHER FALLS BACK, and the fallback is stated - never a zero, which
///     would rank a caching provider as though caching never helped.</para></summary>
public static class HitRateResolution
{
  /// <summary>The lookup for a set of catalog rows, given whatever evidence the caller has.
  ///     </summary>
  /// <param name="entries">The rows being compared.</param>
  /// <param name="report">Our ledger's measurements, or null when the read model is
  ///     unavailable.</param>
  /// <param name="published">The rates providers publish, keyed by
  ///     <see cref="ModelProviderEntry.Key"/>, or null when nothing was fetched.</param>
  /// <param name="fallback">The rate for a pair with no evidence at all. Required: a caller
  ///     must decide what 'nothing measured' means rather than inheriting a constant.</param>
  public static HitRateLookup Resolve(
      IReadOnlyList<ModelProviderEntry> entries,
      ProviderPerformanceReport? report,
      IReadOnlyDictionary<string, PublishedProviderStats>? published,
      HitRateAssumption fallback)
  {
    ArgumentNullException.ThrowIfNull(entries);
    ArgumentNullException.ThrowIfNull(fallback);

    // The report's own rung applies to every pair it measured; its HitRate is the scope-wide
    // figure and is deliberately NOT used here, because a scope-wide rate is exactly the
    // single-number projection this change exists to remove.
    HitRateSource measuredSource = report is null
        ? HitRateSource.Default
        : SourceOf(report.Scope);

    List<KeyValuePair<string, HitRateAssumption>> rates = [];
    foreach (ModelProviderEntry entry in entries.Where(e => e is not null))
    {
      if (ResolveOne(entry, report, published, measuredSource) is { } rate)
      {
        rates.Add(new KeyValuePair<string, HitRateAssumption>(entry.Key, rate));
      }
    }

    return HitRateLookup.Create(rates, fallback);
  }

  /// <summary>One row's rate, or null when there is no evidence for it and the caller's
  ///     fallback should apply.</summary>
  private static HitRateAssumption? ResolveOne(
      ModelProviderEntry entry,
      ProviderPerformanceReport? report,
      IReadOnlyDictionary<string, PublishedProviderStats>? published,
      HitRateSource measuredSource)
  {
    // 1. Our own ledger, for this exact pair. A pair with no input tokens billed has no
    //    measured rate (HitRate is null), which is 'not measured', not 'measured zero'.
    if (report?.Lookup(entry.ModelId, entry.ProviderName)?.HitRate is { } measured)
    {
      return new HitRateAssumption(measured, measuredSource);
    }

    // 2. What the provider publishes for this endpoint.
    if (published is not null && published.TryGetValue(entry.Key, out PublishedProviderStats? stats)
        && stats?.Assumption is { } publishedRate)
    {
      return publishedRate;
    }

    // 3. Nothing: the caller's fallback applies, and it names itself as a default.
    return null;
  }

  /// <summary>The measured rung a usage scope names, for the source that travels with a
  ///     measured rate.</summary>
  private static HitRateSource SourceOf(UsageScope scope) => scope switch
  {
    UsageScope.Session => HitRateSource.Session,
    UsageScope.Workspace => HitRateSource.Workspace,
    UsageScope.Global => HitRateSource.Global,
    _ => HitRateSource.Default,
  };
}
