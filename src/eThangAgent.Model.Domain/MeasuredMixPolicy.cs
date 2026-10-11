namespace eThangAgent.ModelDomain;

/// <summary>One turn's provider-scored token usage.</summary>
public sealed record UsageObservation(int InputTokens, int OutputTokens);

/// <summary>A fired re-selection decision: the measured weight that triggered it and the
///     blended prices (at that weight) of the new winner and the incumbent baseline pair.</summary>
public sealed record ReselectionConsidered(decimal MeasuredWeight, decimal NewBlended, decimal IncumbentBlended);

/// <summary>Pure policy measuring a session's real output/input token mix with an EWMA.
///     Later tasks feed it provider usage and read <see cref="MeasuredWeight"/> once
///     <see cref="IsTrusted"/> — the trust gate keeps a handful of early turns from
///     steering the mix. Thread-safe: observations arrive from provider calls on
///     arbitrary threads. No I/O, no DI — arithmetic only.</summary>
public sealed class MeasuredMixPolicy(decimal? seedWeight)
{
  /// <summary>Weight a session starts from before any observation.</summary>
  public const decimal DefaultWeight = 3m;

  /// <summary>Observed turns required before the measured weight is trusted.</summary>
  public const int TrustGateTurns = 5;

  /// <summary>EWMA smoothing factor applied to each observed turn's weight.</summary>
  public const decimal Alpha = 0.3m;

  /// <summary>Relative margin (Task 3) between measured and configured weight.</summary>
  public const decimal MinMargin = 0.10m;

  private readonly Lock _gate = new();
  private readonly decimal _seedWeight = seedWeight ?? DefaultWeight;
  private decimal _weight = seedWeight ?? DefaultWeight;
  private decimal? _baselineIn;
  private decimal? _baselineOut;
  private long _totalInput;
  private long _totalOutput;
  private int _turns;
  private int _trustTurns;

  /// <summary>Records one turn's usage. A turn with non-positive input tokens is skipped
  ///     entirely: no weight update, no turn count, no totals.</summary>
  public void Observe(UsageObservation observation)
  {
    ArgumentNullException.ThrowIfNull(observation);
    lock (_gate)
    {
      if (observation.InputTokens <= 0)
      {
        return;
      }

      decimal turnWeight = (decimal)observation.OutputTokens / observation.InputTokens;
      _weight = (Alpha * turnWeight) + ((1m - Alpha) * _weight);
      _totalInput += observation.InputTokens;
      _totalOutput += observation.OutputTokens;
      _turns++;
      _trustTurns++;
    }
  }

  /// <summary>True once enough turns have been observed for the measured weight to steer.</summary>
  public bool IsTrusted
  {
    get
    {
      lock (_gate)
      {
        return _trustTurns >= TrustGateTurns;
      }
    }
  }

  /// <summary>The current EWMA weight (output tokens per input token).</summary>
  public decimal MeasuredWeight
  {
    get
    {
      lock (_gate)
      {
        return _weight;
      }
    }
  }

  /// <summary>Accumulated observed totals.</summary>
  public (long TotalInput, long TotalOutput, int Turns) Totals
  {
    get
    {
      lock (_gate)
      {
        return (_totalInput, _totalOutput, _turns);
      }
    }
  }

  /// <summary>Restarts the trust gate without touching the measured weight or totals.</summary>
  public void ResetTrust()
  {
    lock (_gate)
    {
      _trustTurns = 0;
    }
  }

  /// <summary>Evaluates whether the measured token mix has crossed a price break-even
  ///     between the incumbent provider and a new winner, by a margin large enough to
  ///     justify re-selection. Returns null when the weight is untrusted, no entries are
  ///     supplied, no provider changes at the current weight, or the change is within
  ///     <see cref="MinMargin"/>. Read-only over the weight: the EWMA is never touched.
  ///     On fire the baseline price pair is replaced with the new winner's, so a later
  ///     crossing back fires again.</summary>
  public ReselectionConsidered? Evaluate(IReadOnlyList<ModelProviderEntry> qualifyingEntries)
  {
    lock (_gate)
    {
      if (_trustTurns < TrustGateTurns || qualifyingEntries is null || qualifyingEntries.Count == 0)
      {
        return null;
      }

      ModelProviderEntry winner = WinnerAt(qualifyingEntries, _weight);

      (decimal incumbentIn, decimal incumbentOut) = _baselineIn is decimal storedIn && _baselineOut is decimal storedOut
          ? (storedIn, storedOut)
          : PricesOf(WinnerAt(qualifyingEntries, _seedWeight));

      if (winner.PromptPricePerToken == incumbentIn && winner.CompletionPricePerToken == incumbentOut)
      {
        return null;
      }

      decimal newBlended = Blended(winner, _weight);
      decimal incumbentBlended = incumbentIn + (_weight * incumbentOut);

      if (newBlended > incumbentBlended * (1m - MinMargin))
      {
        return null;
      }

      _baselineIn = winner.PromptPricePerToken;
      _baselineOut = winner.CompletionPricePerToken;

      return new ReselectionConsidered(_weight, newBlended, incumbentBlended);
    }
  }

  private static ModelProviderEntry WinnerAt(IReadOnlyList<ModelProviderEntry> entries, decimal weight)
      => entries
          .OrderBy(e => Blended(e, weight))
          .ThenBy(e => e.ModelId, StringComparer.Ordinal)
          .ThenBy(e => e.ProviderName, StringComparer.Ordinal)
          .First();

  private static decimal Blended(ModelProviderEntry entry, decimal weight)
      => entry.PromptPricePerToken + (weight * entry.CompletionPricePerToken);

  private static (decimal In, decimal Out) PricesOf(ModelProviderEntry entry)
      => (entry.PromptPricePerToken, entry.CompletionPricePerToken);
}
