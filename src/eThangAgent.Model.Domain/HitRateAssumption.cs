namespace eThangAgent.ModelDomain;

/// <summary>Where the cache-hit rate a comparison projects prices at came from (issue #296,
///     D6, which inherits its rule from the parent's D2: the hit rate is stated, never
///     implied).
///     <para>WHY THE SOURCE IS A FIRST-CLASS FACT: the same provider is the cheap choice at a
///     97% hit rate and the expensive one at 0%, so a price projection is only meaningful
///     together with the assumption behind it. Naming the assumption is what turns a number
///     into a claim the user can check - and override.</para>
///     <para><see cref="Global"/> exists because the read model (issue #295) has THREE
///     measured rungs, not two: a fresh session in a fresh workspace still has the whole
///     ledger behind it, and collapsing that rung into <see cref="Default"/> would present a
///     real measurement as an invented constant - the same lie in the other direction.</para>
///     <para><see cref="Manual"/> is the user's override (D6's most instructive
///     interaction): the ranking is recomputed at a rate the user chose, and the surface says
///     so rather than passing the override off as a measurement.</para></summary>
public enum HitRateSource
{
  /// <summary>The session's own measured cache-hit share.</summary>
  Session,

  /// <summary>The workspace's measured share, used when the session has billed nothing yet.</summary>
  Workspace,

  /// <summary>The whole ledger's measured share, used when the workspace has no history either.</summary>
  Global,

  /// <summary>Nothing in the ledger has billed an input token, so the documented default
  ///     applies. The surface says so: this is the only rate nobody measured.</summary>
  Default,

  /// <summary>The user typed the rate. It is an assumption stated by a person, not a
  ///     measurement, and the surface distinguishes the two.</summary>
  Manual,
}

/// <summary>The cache-hit rate a provider comparison projects its prices at, together with
///     where that rate came from (issue #296, D6).
///     <para>THE RATE IS ALWAYS CARRIED WITH ITS SOURCE, never alone: a projection built on an
///     unstated assumption is a guess wearing a decimal point, which is the failure the whole
///     parent issue exists to remove. A caller that wants to show the price must show this
///     record's <see cref="Describe"/> text beside it.</para>
///     <para>THE DEFAULT IS DELIBERATELY MID-RANGE (<see cref="DefaultRate"/>). Zero would
///     make every provider look as though caching never helps, hiding the single largest
///     difference this surface exists to show; a high figure would project one workspace's
///     measured behaviour onto every model and every user. Half is stated, not implied, and
///     the surface names it as the default.</para></summary>
/// <param name="Rate">The share of input tokens expected to be cache hits, in [0,1].</param>
/// <param name="Source">Where the rate came from.</param>
public sealed record HitRateAssumption(decimal Rate, HitRateSource Source)
{
  /// <summary>The documented default, used only when the ledger has measured nothing.</summary>
  public const decimal DefaultRate = 0.5m;

  /// <summary>The default assumption: mid-range, and named as a default rather than passed
  ///     off as a measurement.</summary>
  public static HitRateAssumption Fallback { get; } = new(DefaultRate, HitRateSource.Default);

  /// <summary>The assumption a performance report supports: the report's own measured rate
  ///     at the rung that answered it, or the documented default when nothing has been
  ///     measured (a null rate means no input token was ever billed in scope).
  ///     <para>The report's <see cref="ProviderPerformanceReport.Scope"/> is the rung that
  ///     ACTUALLY answered - possibly a fallback rung - so the source travels with the rate
  ///     instead of being re-derived from what the caller asked for.</para></summary>
  public static HitRateAssumption From(ProviderPerformanceReport report)
  {
    ArgumentNullException.ThrowIfNull(report);
    return report.HitRate is { } measured
        ? new HitRateAssumption(measured, SourceOf(report.Scope))
        : Fallback;
  }

  /// <summary>The user's override: an assumption a person stated, which the surface must
  ///     never present as a measurement.</summary>
  public static HitRateAssumption Manual(decimal rate)
  {
    Validate(rate);
    return new HitRateAssumption(rate, HitRateSource.Manual);
  }

  /// <summary>The rate as a whole-percent figure for display and for the editable field
  ///     (one decimal, so 97.7% survives the round trip through the text box).</summary>
  public string PercentText => (Rate * 100m).ToString("0.#",
      System.Globalization.CultureInfo.InvariantCulture) + "%";

  /// <summary>The one-line statement of the assumption and its provenance, e.g.
  ///     <c>session measured 97.7%</c> or <c>default 50% (nothing measured yet)</c>.
  ///     <para>EVERY SOURCE GETS ITS OWN SENTENCE: a surface that rendered a measured rate
  ///     and an invented one identically would undo the distinction this record exists to
  ///     carry.</para></summary>
  public string Describe() => Source switch
  {
    HitRateSource.Session => $"session measured {PercentText}",
    HitRateSource.Workspace => $"workspace measured {PercentText}",
    HitRateSource.Global => $"all sessions measured {PercentText}",
    HitRateSource.Default => $"default {PercentText} (nothing measured yet)",
    HitRateSource.Manual => $"manual {PercentText}",
    _ => $"{PercentText}",
  };

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
