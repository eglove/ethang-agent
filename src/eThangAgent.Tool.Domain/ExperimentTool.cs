using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>The scientific loop's record surface: hypotheses, predictions, verdicts,
///     observations, conclusions, and skips appended to one plan's ledger
///     (<c>sci.&lt;slug&gt;/ledger</c>), plus a listing that reports the open-prediction
///     count the finish gate reads. Enforcement sits on the two transitions that
///     matter: a prediction must name a falsifying check, and a confirmed or refuted
///     verdict must name its evidence. Everything else is bookkeeping.</summary>
public sealed class ExperimentTool(IExperimentLedgerStore store) : ITool
{
  private readonly IExperimentLedgerStore _store = store ?? throw new ArgumentNullException(nameof(store));

  public ToolDefinition Definition { get; } = new(
      "experiment",
      "Record the scientific loop's hypotheses, predictions, verdicts, observations, conclusions, " +
      "and skips in the plan's append-only ledger sci.<slug>/ledger. timeoutSeconds, action, and " +
      "slug are mandatory; slug is the plan slug (no '/' and no whitespace). action is exactly " +
      "record-hypothesis, record-prediction, record-verdict, record-observation, " +
      "record-conclusion, record-skip, list, or summary (case-sensitive). record-hypothesis requires text; " +
      "id is optional and must be a free positive integer (otherwise the next free id is " +
      "assigned). record-prediction requires text, falsifiedBy (the exact command whose output " +
      "would refute the prediction), and id naming an existing hypothesis. record-verdict " +
      "requires id naming an existing prediction, verdict exactly confirmed, refuted, or " +
      "inconclusive, and evidence (required and non-empty for confirmed and refuted, optional " +
      "for inconclusive); a prediction takes exactly one verdict. record-observation, " +
      "record-conclusion, and record-skip require text. record-skip takes an optional id naming " +
      "an existing prediction (the skip closes it); without id the skip is a bare operator " +
      "escape that closes nothing. list requires only slug and prints " +
      "'[experiment] sci.<slug>/ledger: <N> record(s), <M> open prediction(s)' followed by at " +
      "most 50 ledger records verbatim - past 50 a marker names the dropped count; the header " +
      "counts are ALWAYS complete - or '[experiment] sci.<slug>/ledger: empty' when the ledger " +
      "is empty. summary requires only slug and prints the header line alone - the cheap " +
      "finish-gate check. <M> counts predictions carrying neither a verdict nor a skip. Every recording " +
      "action prints one annotation line: '[experiment] <kind> <id> recorded in " +
      "sci.<slug>/ledger'. Unknown ids fail with HypothesisNotFound or PredictionNotFound; a " +
      "second verdict on one prediction fails with PredictionAlreadyJudged. Errors begin with " +
      "`Error [Code]:`.",
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
          new ToolParameter("action", ToolParameterType.Text,
              "Exactly " + ExperimentInput.ActionVocabulary + " (case-sensitive)."),
          new ToolParameter("slug", ToolParameterType.Text,
              "Always: the plan slug; the ledger key is sci.<slug>/ledger (no '/' and no whitespace)."),
          new ToolParameter("id", ToolParameterType.WholeNumber,
              "record-hypothesis: optional free hypothesis id. record-prediction: the hypothesis " +
              "id it tests. record-verdict: the prediction id it judges. record-skip: optional " +
              "prediction id it closes."),
          new ToolParameter("text", ToolParameterType.Text,
              "record-hypothesis, record-prediction, record-observation, record-conclusion, " +
              "record-skip: the record's text (single line)."),
          new ToolParameter("falsifiedBy", ToolParameterType.Text,
              "record-prediction only: the exact command whose output would refute the prediction."),
          new ToolParameter("evidence", ToolParameterType.Text,
              "record-verdict only: the concrete evidence (command + observed output); required " +
              "for confirmed and refuted, optional for inconclusive."),
          new ToolParameter("verdict", ToolParameterType.Text,
              "record-verdict only: exactly confirmed, refuted, or inconclusive (case-sensitive)."),
      ],
      [ToolTimeout.ParameterName, "action"]);

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<ExperimentInput> parsed = ExperimentInput.Create(input.JsonArguments);
    if (!parsed.IsSuccess)
    {
      return Task.FromResult(Err(parsed.Error));
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    return !budget.IsSuccess
      ? Task.FromResult(Err(budget.Error))
      : ToolExecution.RunAsync(input.Name, budget.Value.Timeout, token =>
        RunAsync(parsed.Value, token), ct);
  }

  private async Task<ToolResult> RunAsync(ExperimentInput input, CancellationToken ct)
  {
    string key = ExperimentLedger.KeyFor(input.Slug);
    LoadedLedger loaded = await LoadAsync(key, ct).ConfigureAwait(false);
    if (loaded.Error is not null)
    {
      return loaded.Error;
    }

    IReadOnlyList<ExperimentRecord> records = loaded.Records;

    return input.Action is ExperimentAction.List or ExperimentAction.Summary
        ? ReadAsync(input, key, records)
        : await RecordAsync(input, key, records, ct).ConfigureAwait(false);

  }


  /// <summary>The read actions' router: list echoes the ledger (capped), summary
  ///     prints the header alone (issue #144).</summary>
  private static ToolResult ReadAsync(ExperimentInput input, string key,
      IReadOnlyList<ExperimentRecord> records)
  {
    return input.Action is ExperimentAction.Summary
        ? Summarize(key, records)
        : List(key, records);
  }


  private async Task<LoadedLedger> LoadAsync(string key, CancellationToken ct)
  {
    Result<string> read = await _store.ReadAsync(key, ct).ConfigureAwait(false);
    return read.IsSuccess
      ? new LoadedLedger(ExperimentLedger.Parse(read.Value), null)
      : FailedLoad(read.Error);
  }


  private readonly record struct LoadedLedger(IReadOnlyList<ExperimentRecord> Records, ToolResult? Error);

  private static LoadedLedger FailedLoad(DomainError error) =>
      error.Code == "KeyNotFound"
        ? new LoadedLedger([], null)
        : new LoadedLedger([], Err(error));



  private async Task<ToolResult> RecordAsync(ExperimentInput input, string key,
      IReadOnlyList<ExperimentRecord> records, CancellationToken ct)
  {
    Result<RecordedLine> line = BuildLine(input, records);
    return line.IsSuccess
      ? await AppendAsync(key, line.Value, ct).ConfigureAwait(false)
      : Err(line.Error);
  }

  /// <summary>Appends the rendered line and reports the assigned id. CAS with a null
  ///     expectedVersion: the store re-reads the row and CASes on that fresh read, so a
  ///     lost update is impossible. A version memo would manufacture spurious
  ///     VersionConflict — the ledger is multi-writer (children append too).</summary>
  private async Task<ToolResult> AppendAsync(string key, RecordedLine line, CancellationToken ct)
  {
    Result<int> appended = await _store.AppendAsync(key, line.Line, null, ct).ConfigureAwait(false);
    return appended.IsSuccess
      ? new ToolResult(
          $"[experiment] {ExperimentLedger.KindWord(line.Kind)} {line.Id} recorded in {key}", false)
      : Err(appended.Error);
  }

  /// <summary>One rendered ledger line plus the kind and id the annotation line reports,
  ///     so the id is computed exactly once.</summary>
  private readonly record struct RecordedLine(ExperimentRecordKind Kind, int Id, string Line)
  {
    public static RecordedLine Of(ExperimentRecordKind kind, int id, string line) => new(kind, id, line);
  }

  /// <summary>Validates the call against the loaded records and renders the exact ledger
  ///     line. A failure here means nothing is appended.</summary>
  private static Result<RecordedLine> BuildLine(ExperimentInput input, IReadOnlyList<ExperimentRecord> records)
  {
    return input.Action switch
    {
      ExperimentAction.RecordHypothesis => HypothesisLine(input, records),
      ExperimentAction.RecordPrediction => PredictionLine(input, records),
      ExperimentAction.RecordVerdict => VerdictLine(input, records),
      ExperimentAction.RecordObservation => SimpleLine(input, records, ExperimentRecordKind.Observation),
      ExperimentAction.RecordConclusion => SimpleLine(input, records, ExperimentRecordKind.Conclusion),
      ExperimentAction.RecordSkip => SkipLine(input, records),
      // List never reaches the append path (RunAsync branches on it first); the switch
      // stays exhaustive, so the impossible case throws rather than recording nothing.
      ExperimentAction.List
      or ExperimentAction.Summary
        => throw new InvalidOperationException(
            "'list' and 'summary' record nothing; RunAsync branches on them before building a line."),
      _ => throw new InvalidOperationException("Only recording actions build a ledger line."),
    };
  }

  private static Result<RecordedLine> HypothesisLine(ExperimentInput input, IReadOnlyList<ExperimentRecord> records)
  {
    if (input.Id is { } supplied)
    {
      return ExperimentLedger.Contains(records, ExperimentRecordKind.Hypothesis, supplied)
        ? Failure($"Hypothesis id {supplied} is already used in this ledger. Omit 'id' to take " +
            "the next free hypothesis id.")
        : Result.Success(RecordedLine.Of(ExperimentRecordKind.Hypothesis, supplied,
            $"hypothesis {supplied}: {input.Text}"));
    }

    int id = ExperimentLedger.NextId(records, ExperimentRecordKind.Hypothesis);
    return Result.Success(RecordedLine.Of(ExperimentRecordKind.Hypothesis, id,
        $"hypothesis {id}: {input.Text}"));
  }

  private static Result<RecordedLine> PredictionLine(ExperimentInput input, IReadOnlyList<ExperimentRecord> records)
  {
    int hypothesisId = input.Id!.Value;
    if (!ExperimentLedger.Contains(records, ExperimentRecordKind.Hypothesis, hypothesisId))
    {
      return Result.Failure<RecordedLine>(new DomainError("HypothesisNotFound",
          $"No hypothesis with id {hypothesisId} in this ledger. A prediction must name an " +
          "existing hypothesis id; use action list to see the current ids."));
    }

    int id = ExperimentLedger.NextId(records, ExperimentRecordKind.Prediction);
    return Result.Success(RecordedLine.Of(ExperimentRecordKind.Prediction, id,
        $"prediction {id} (hypothesis {hypothesisId}): {input.Text}" +
        $"{ExperimentLedger.FalsifiedByMarker}{input.FalsifiedBy}"));
  }

  private static Result<RecordedLine> VerdictLine(ExperimentInput input, IReadOnlyList<ExperimentRecord> records)
  {
    int predictionId = input.Id!.Value;
    if (!ExperimentLedger.Contains(records, ExperimentRecordKind.Prediction, predictionId))
    {
      return Result.Failure<RecordedLine>(new DomainError("PredictionNotFound",
          $"No prediction with id {predictionId} in this ledger. A verdict must name an " +
          "existing prediction id; use action list to see the current ids."));
    }

    bool judged = records.Any(r => r.Kind == ExperimentRecordKind.Verdict && r.Reference == predictionId);
    if (judged)
    {
      return Result.Failure<RecordedLine>(new DomainError("PredictionAlreadyJudged",
          $"Prediction {predictionId} already carries a verdict. One verdict per prediction: " +
          "record further observations, or a conclusion, instead."));
    }

    int id = ExperimentLedger.NextId(records, ExperimentRecordKind.Verdict);
    string word = VerdictWord(input.Verdict!.Value);
    string text = input.Evidence is { } evidence
      ? $"verdict {id} (prediction {predictionId}): {word}{ExperimentLedger.EvidenceMarker}{evidence}"
      : $"verdict {id} (prediction {predictionId}): {word}";
    return Result.Success(RecordedLine.Of(ExperimentRecordKind.Verdict, id, text));
  }

  private static Result<RecordedLine> SimpleLine(ExperimentInput input, IReadOnlyList<ExperimentRecord> records,
      ExperimentRecordKind kind)
  {
    int id = ExperimentLedger.NextId(records, kind);
    return Result.Success(RecordedLine.Of(kind, id,
        $"{ExperimentLedger.KindWord(kind)} {id}: {input.Text}"));
  }

  private static Result<RecordedLine> SkipLine(ExperimentInput input, IReadOnlyList<ExperimentRecord> records)
  {
    int id = ExperimentLedger.NextId(records, ExperimentRecordKind.Skip);
    if (input.Id is not { } predictionId)
    {
      // A bare skip is the operator escape valve: it records the bypass and closes nothing.
      return Result.Success(RecordedLine.Of(ExperimentRecordKind.Skip, id, $"skip {id}: {input.Text}"));
    }

    return ExperimentLedger.Contains(records, ExperimentRecordKind.Prediction, predictionId)
      ? Result.Success(RecordedLine.Of(ExperimentRecordKind.Skip, id,
          $"skip {id} (prediction {predictionId}): {input.Text}"))
      : Result.Failure<RecordedLine>(new DomainError("PredictionNotFound",
          $"No prediction with id {predictionId} in this ledger. A skip closes an existing " +
          "prediction id; omit 'id' to record a bare operator skip."));
  }

  /// <summary>The list cap (issue #144): a ledger is arbitrary-length - one real ledger
  ///     in this workspace is 51 KB - so the echo stops at <see cref="ListCap"/> records
  ///     and a marker names the dropped count and the cheaper surface (summary). The
  ///     HEADER is never truncated: its counts come from the whole record set, and the
  ///     finish gate reads exactly them.</summary>
  internal const int ListCap = 50;

  private static ToolResult List(string key, IReadOnlyList<ExperimentRecord> records)
  {
    if (records.Count == 0)
    {
      return new ToolResult($"[experiment] {key}: empty", false);
    }

    int open = ExperimentLedger.OpenPredictions(records);
    List<string> lines = new(Math.Min(records.Count, ListCap) + 2)
    {
        $"[experiment] {key}: {records.Count} record(s), {open} open prediction(s)",
    };
    lines.AddRange(records.Take(ListCap).Select(r => r.Raw));
    int dropped = records.Count - ListCap;
    if (dropped > 0)
    {
      lines.Add($"[experiment] ... {dropped} more record(s) not shown; "
          + "the header counts are complete - use action 'summary' for the counts alone, "
          + "or read the ledger directly for the full history.");
    }

    return new ToolResult(string.Join("\n", lines), false);
  }

  /// <summary>The summary surface (issue #144 B2): the header the finish gate reads,
  ///     with no record bodies - the cheap answer to "is the gate satisfied?".</summary>
  private static ToolResult Summarize(string key, IReadOnlyList<ExperimentRecord> records)
  {
    if (records.Count == 0)
    {
      return new ToolResult($"[experiment] {key}: empty", false);
    }

    int open = ExperimentLedger.OpenPredictions(records);
    return new ToolResult($"[experiment] {key}: {records.Count} record(s), {open} open prediction(s)", false);
  }

  /// <summary>The InvalidParameterValue failure for a call this action cannot record.</summary>
  private static Result<RecordedLine> Failure(string message) =>
      Result.Failure<RecordedLine>(new DomainError(ToolErrorCodes.InvalidParameterValue, message));

  private static string VerdictWord(ExperimentVerdict verdict) => verdict switch
  {
    ExperimentVerdict.Confirmed => "confirmed",
    ExperimentVerdict.Refuted => "refuted",
    ExperimentVerdict.Inconclusive => "inconclusive",
    // Unnamed enum values cannot occur.
    _ => throw new InvalidOperationException("Unknown experiment verdict."),
  };

  private static ToolResult Err(DomainError error) => new($"Error [{error.Code}]: {error.Message}", true);
}
