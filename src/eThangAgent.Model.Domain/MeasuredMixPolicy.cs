namespace eThangAgent.ModelDomain;

/// <summary>One turn's provider-scored token usage.</summary>
public sealed record UsageObservation(int InputTokens, int OutputTokens);

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
  private decimal _weight = seedWeight ?? DefaultWeight;
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
}
