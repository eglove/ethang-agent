namespace eThangAgent.ModelDomain;

/// <summary>Where the cache-hit rate a provider row's price is projected at came from
///     (issue #296, D6, inheriting the parent's D2: the hit rate is stated, never implied).
///     <para>WHY THE SOURCE IS A FIRST-CLASS FACT: the same provider is the cheap choice at a
///     97% hit rate and the expensive one at 0%, so a price projection is only meaningful
///     together with the assumption behind it. Naming the assumption is what turns a number
///     into a claim the user can check.</para>
///     <para>THE ORDER OF THE MEMBERS IS THE FALLBACK ORDER, most trustworthy first: our own
///     ledger for this exact pair, then the wider ledger rungs, then what the provider
///     publishes, then the documented default. A row walks this chain and stops at the first
///     rung that answers.</para>
///     <para><see cref="Published"/> exists because a fresh session has measured nothing and
///     a global default is a poor guess for a pair that has never run: OpenRouter publishes a
///     per-provider cache-hit rate measured across ITS traffic, which is real evidence about
///     the pair even when our ledger has none. It is not OUR measurement, so it is named
///     differently rather than passed off as one.</para></summary>
public enum HitRateSource
{
  /// <summary>This session's own measured cache-hit share for this pair.</summary>
  Session,

  /// <summary>This workspace's measured share for this pair, used when the session has
  ///     billed nothing yet.</summary>
  Workspace,

  /// <summary>The whole ledger's measured share for this pair, used when the workspace has
  ///     no history for it either.</summary>
  Global,

  /// <summary>The rate the provider's own published statistics report for this endpoint.
  ///     Real evidence, but not our measurement: it describes OpenRouter's whole traffic for
  ///     the endpoint, not this session's prompt shapes.</summary>
  Published,

  /// <summary>Nothing measured this pair and the provider publishes no rate, so the
  ///     documented default applies. The surface says so: this is the only rate nobody
  ///     measured and nobody published.</summary>
  Default,
}

/// <summary>The cache-hit rate a provider row's price is projected at, together with where
///     that rate came from (issue #296, D6).
///     <para>THE RATE IS ALWAYS CARRIED WITH ITS SOURCE, never alone: a projection built on an
///     unstated assumption is a guess wearing a decimal point, which is the failure the whole
///     parent issue exists to remove. A caller that wants to show the price must show this
///     record's <see cref="Describe"/> text beside it.</para>
///     <para>THE DEFAULT IS DELIBERATELY MID-RANGE (<see cref="DefaultRate"/>). Zero would
///     make every provider look as though caching never helps, hiding the single largest
///     difference this surface exists to show; a high figure would project one workspace's
///     measured behaviour onto every model and every user. Half is stated, not implied, and
///     the surface names it as the default.</para>
///     <para>THERE IS NO USER-SUPPLIED RATE. A hand-typed assumption was removed: the rate is
///     a property of the model, the provider, and the workload, so the harness resolves it
///     from evidence (our ledger, then the provider's published figures) and states which
///     rung answered. An editable box invited the user to invent a number the harness already
///     knew better than they did.</para></summary>
/// <param name="Rate">The share of input tokens expected to be cache hits, in [0,1].</param>
/// <param name="Source">Where the rate came from.</param>
public sealed record HitRateAssumption(decimal Rate, HitRateSource Source)
{
  /// <summary>The documented default, used only when nothing measured and nothing was
  ///     published.</summary>
  public const decimal DefaultRate = 0.5m;

  /// <summary>The default assumption: mid-range, and named as a default rather than passed
  ///     off as a measurement.</summary>
  public static HitRateAssumption Fallback { get; } = new(DefaultRate, HitRateSource.Default);

  /// <summary>The assumption a measured pair supports: the pair's own measured rate at the
  ///     rung that answered it, or null when nothing measured it (a null rate means no input
  ///     token was ever billed for that pair).
  ///     <para>The report's <see cref="ProviderPerformanceReport.Scope"/> is the rung that
  ///     ACTUALLY answered - possibly a fallback rung - so the source travels with the rate
  ///     instead of being re-derived from what the caller asked for.</para></summary>
  public static HitRateAssumption? From(ProviderPerformanceReport report)
  {
    ArgumentNullException.ThrowIfNull(report);
    return report.HitRate is { } measured
        ? new HitRateAssumption(measured, SourceOf(report.Scope))
        : null;
  }

  /// <summary>The rate a provider publishes for one endpoint, as real evidence about a pair
  ///     this session has never run. Named <see cref="HitRateSource.Published"/> so no
  ///     surface can present OpenRouter's fleet-wide figure as our own measurement.</summary>
  public static HitRateAssumption FromPublished(decimal rate)
  {
    Validate(rate);
    return new HitRateAssumption(rate, HitRateSource.Published);
  }

  /// <summary>The rate as a whole-percent figure for display (one decimal, so 97.7% survives
  ///     the round trip).</summary>
  public string PercentText => (Rate * 100m).ToString("0.#",
      System.Globalization.CultureInfo.InvariantCulture) + "%";

  /// <summary>The one-line statement of the assumption and its provenance, e.g.
  ///     <c>session measured 97.7%</c>, <c>OpenRouter published 62.9%</c>, or
  ///     <c>default 50% (nothing measured yet)</c>.
  ///     <para>EVERY SOURCE GETS ITS OWN SENTENCE: a surface that rendered a measured rate,
  ///     a published rate, and an invented default identically would undo the distinction
  ///     this record exists to carry.</para></summary>
  public string Describe() => Source switch
  {
    HitRateSource.Session => $"session measured {PercentText}",
    HitRateSource.Workspace => $"workspace measured {PercentText}",
    HitRateSource.Global => $"all sessions measured {PercentText}",
    HitRateSource.Published => $"OpenRouter published {PercentText}",
    HitRateSource.Default => $"default {PercentText} (nothing measured yet)",
    _ => $"{PercentText}",
  };

  /// <summary>True when this rate rests on evidence rather than the documented default.
  ///     A surface uses it to say 'this is a guess' only where it really is one.</summary>
  public bool IsEvidence => Source != HitRateSource.Default;

  /// <summary>A hit rate outside [0,1] is a caller's arithmetic bug, not a domain failure:
  ///     no provider, session, or catalog can produce one, so it is thrown rather than
  ///     clamped - the same rule <see cref="EffectivePrice"/> follows, because clamping would
  ///     turn a bug into a plausible-looking price.</summary>
  private static void Validate(decimal rate)
  {
    ArgumentOutOfRangeException.ThrowIfNegative(rate);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(rate, 1m);
  }

  /// <summary>The measured rung a usage scope names. Every scope is a MEASUREMENT - the
  ///     default is not a scope, which is why it is not a member of
  ///     <see cref="UsageScope"/>.</summary>
  private static HitRateSource SourceOf(UsageScope scope) => scope switch
  {
    UsageScope.Session => HitRateSource.Session,
    UsageScope.Workspace => HitRateSource.Workspace,
    UsageScope.Global => HitRateSource.Global,
    _ => HitRateSource.Default,
  };
}
