using System.Globalization;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>The thresholds a chain telemetry report is judged against (issue #309 D4/D5), as a
///     DURABLE ARTIFACT rather than a claim about history.
///     <para>WHY THIS TYPE EXISTS AT ALL: 'I chose the thresholds before I saw the number' is
///     unverifiable — it is a statement about the order of two events, one of which leaves no
///     trace. So the thresholds are written to a state key BEFORE the first report, and that
///     key's timestamp is the evidence. This type is the PARSER, which is what makes the
///     ordering structural rather than advisory: <see cref="ChainTelemetryReport"/> refuses to
///     compute anything without a set, so a report cannot be produced first and given
///     thresholds afterwards.</para>
///     <para>PARSED STRICTLY, THE <c>TrustedDirectoryPreferences</c> PRECEDENT: every member is
///     REQUIRED, an unknown key is a named refusal, and a value that is not a number is a named
///     refusal. A lenient parser would let a typo silently restore a default nobody chose, which
///     is exactly the 'thresholds chosen after the fact' failure this whole decision prevents.
///     An incomplete set is refused rather than completed.</para>
///     <para>THE NUMBERS ARE A JUDGMENT AND THE USER IS THE GATE ON THEM. Nothing here can
///     check that a 25% intervention ceiling is the right ceiling; what it can check is that
///     one existed before the measurement did.</para></summary>
public sealed record ChainTelemetryThresholds
{
  /// <summary>Every key the artifact must carry, in the order the report renders them.
  ///     <para>NAMED AS A LIST so the parser's completeness check and the missing-key refusal
  ///     read the same source: a member added to the record without being added here would be
  ///     unparseable, and the omission surfaces as a failing test rather than as a threshold
  ///     nobody set.</para></summary>
  public static readonly string[] RequiredKeys =
  [
      InterventionShareMaxKey,
      MedianCompletedStepsMinKey,
      ParkShareMaxKey,
      TopParkReasonShareMaxKey,
      FirstAttemptSuccessMinKey,
      CapReachedShareMaxKey,
      ReviewDenyShareMinKey,
      ReviewDenyShareMaxKey,
      PlanChangedAfterDenyMinKey,
      CostPerStepRatioMaxKey,
      StartedSettledRatioMinKey,
      WallClockRatioMaxKey,
  ];

  private const string InterventionShareMaxKey = "intervention-share-max";
  private const string MedianCompletedStepsMinKey = "median-completed-steps-min";
  private const string ParkShareMaxKey = "park-share-max";
  private const string TopParkReasonShareMaxKey = "top-park-reason-share-max";
  private const string FirstAttemptSuccessMinKey = "first-attempt-success-min";
  private const string CapReachedShareMaxKey = "cap-reached-share-max";
  private const string ReviewDenyShareMinKey = "review-deny-share-min";
  private const string ReviewDenyShareMaxKey = "review-deny-share-max";
  private const string PlanChangedAfterDenyMinKey = "plan-changed-after-deny-min";
  private const string CostPerStepRatioMaxKey = "cost-per-step-ratio-vs-baseline-max";
  private const string StartedSettledRatioMinKey = "started-settled-ratio-min";
  private const string WallClockRatioMaxKey = "wall-clock-ratio-vs-baseline-max";

  /// <summary>The state key the thresholds live at, under the chain's own ledger namespace so
  ///     a chain's thresholds and its ledger are found together.
  ///     <para>ONE DERIVATION SITE: this method builds it from the ledger slug, so the
  ///     key the implementing session wrote and the key the report reads cannot disagree — the
  ///     <see cref="ChainLedgerSlug.KeyFor"/> precedent.</para></summary>
  /// <param name="chainTitle">The chain's title, which also derives its ledger slug.</param>
  public static string KeyFor(string? chainTitle) =>
      "sci." + ChainLedgerSlug.For(chainTitle) + "/thresholds";

  /// <summary>The most chains that may park before the intervention ceiling is breached.</summary>
  public required decimal InterventionShareMax { get; init; }

  /// <summary>The fewest steps a completed chain must finish, in the median.</summary>
  public required decimal MedianCompletedStepsMin { get; init; }

  /// <summary>The most chains that may park.</summary>
  public required decimal ParkShareMax { get; init; }

  /// <summary>The largest share one park reason may hold of all parks.</summary>
  public required decimal TopParkReasonShareMax { get; init; }

  /// <summary>The fewest steps that must succeed on their first attempt.</summary>
  public required decimal FirstAttemptSuccessMin { get; init; }

  /// <summary>The most steps that may exhaust the retry cap.</summary>
  public required decimal CapReachedShareMax { get; init; }

  /// <summary>The lowest deny rate that is still a reviewer rather than a rubber stamp.</summary>
  public required decimal ReviewDenyShareMin { get; init; }

  /// <summary>The highest deny rate before the implementer or the brief is the problem.</summary>
  public required decimal ReviewDenyShareMax { get; init; }

  /// <summary>The fewest denials that must actually change the plan.</summary>
  public required decimal PlanChangedAfterDenyMin { get; init; }

  /// <summary>The most a completed chain step may cost, as a multiple of the baseline.</summary>
  public required decimal CostPerStepRatioMax { get; init; }

  /// <summary>The fewest started steps that must settle.</summary>
  public required decimal StartedSettledRatioMin { get; init; }

  /// <summary>The most a completed step may take, as a multiple of the baseline.</summary>
  public required decimal WallClockRatioMax { get; init; }

  /// <summary>Parses the stored artifact.
  ///     <para>REFUSALS ARE NAMED AND DISTINCT, because each is a different repair: a MISSING
  ///     KEY means the artifact predates the metric or was hand-edited, an UNKNOWN KEY means
  ///     someone invented one, and a MALFORMED VALUE means the number was not a number. Folding
  ///     them into one 'invalid thresholds' message would leave the user guessing which.</para>
  ///     <para>COMMENTS ARE LEGAL (a leading '#') because the artifact is prose with numbers in
  ///     it — the session that wrote it said WHY each threshold exists, and a parser that
  ///     demanded bare key/value pairs would have thrown away the reasoning.</para></summary>
  /// <param name="text">The stored key's value.</param>
  public static Result<ChainTelemetryThresholds> Parse(string? text)
  {
    if (string.IsNullOrWhiteSpace(text))
    {
      return Result.Failure<ChainTelemetryThresholds>(new DomainError(Code,
          "the thresholds artifact is empty, so nothing could have been fixed before the "
          + "measurement. Write it first: " + Describe(RequiredKeys) + "."));
    }

    Dictionary<string, decimal> values = new(StringComparer.Ordinal);
    foreach (string raw in text.Split('\n'))
    {
      Result<bool> read = ReadLine(raw, values);
      if (!read.IsSuccess)
      {
        return Result.Failure<ChainTelemetryThresholds>(read.Error);
      }
    }

    return Build(values);
  }

  /// <summary>Reads one line into <paramref name="values"/>; a blank or comment line adds
  ///     nothing. Extracted so the parse loop stays one statement per concern.</summary>
  private static Result<bool> ReadLine(string raw, Dictionary<string, decimal> values)
  {
    string line = raw.Trim();
    if (line.Length == 0 || line[0] == '#')
    {
      return Result.Success(true);
    }

    int equals = line.IndexOf('=', StringComparison.Ordinal);
    return equals <= 0
        ? Result.Failure<bool>(new DomainError(Code,
            $"'{line}' is not 'key = value'. Every non-comment line states one threshold."))
        : Store(line[..equals].Trim(), line[(equals + 1)..].Trim(), values);
  }

  /// <summary>Validates and stores one key/value pair. An unknown key is refused rather than
  ///     ignored: a threshold somebody invented is one the report would never read, so
  ///     accepting it would let a user believe a bound is in force when nothing checks it.
  ///     </summary>
  private static Result<bool> Store(string key, string value, Dictionary<string, decimal> values)
  {
    if (!RequiredKeys.Contains(key, StringComparer.Ordinal))
    {
      return Result.Failure<bool>(new DomainError(Code,
          $"'{key}' is not a telemetry threshold. Known keys: " + Describe(RequiredKeys) + "."));
    }

    if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture,
        out decimal parsed))
    {
      return Result.Failure<bool>(new DomainError(Code,
          $"'{key}' must be a number, but reads '{value}'."));
    }

    values[key] = parsed;
    return Result.Success(true);
  }

  /// <summary>Builds the record from the parsed values, refusing an incomplete set.
  ///     <para>THE MISSING-KEY REFUSAL NAMES WHAT IS MISSING rather than saying the artifact
  ///     is invalid: a user who added one threshold to an old artifact needs to know which
  ///     ones are still absent.</para></summary>
  private static Result<ChainTelemetryThresholds> Build(Dictionary<string, decimal> values)
  {
    string[] missing = [.. RequiredKeys.Where(key => !values.ContainsKey(key))];
    return missing.Length > 0
        ? Result.Failure<ChainTelemetryThresholds>(new DomainError(Code,
            "the thresholds artifact is incomplete; missing: " + Describe(missing) + "."))
        : Result.Success(new ChainTelemetryThresholds
        {
          InterventionShareMax = values[InterventionShareMaxKey],
          MedianCompletedStepsMin = values[MedianCompletedStepsMinKey],
          ParkShareMax = values[ParkShareMaxKey],
          TopParkReasonShareMax = values[TopParkReasonShareMaxKey],
          FirstAttemptSuccessMin = values[FirstAttemptSuccessMinKey],
          CapReachedShareMax = values[CapReachedShareMaxKey],
          ReviewDenyShareMin = values[ReviewDenyShareMinKey],
          ReviewDenyShareMax = values[ReviewDenyShareMaxKey],
          PlanChangedAfterDenyMin = values[PlanChangedAfterDenyMinKey],
          CostPerStepRatioMax = values[CostPerStepRatioMaxKey],
          StartedSettledRatioMin = values[StartedSettledRatioMinKey],
          WallClockRatioMax = values[WallClockRatioMaxKey],
        });
  }

  private static string Describe(IReadOnlyList<string> keys) => string.Join(", ", keys);

  /// <summary>The named refusal code: a report that ran without fixed thresholds would be
  ///     exactly the unfalsifiable claim the issue opens with.</summary>
  public const string Code = "TelemetryThresholdsInvalid";
}
