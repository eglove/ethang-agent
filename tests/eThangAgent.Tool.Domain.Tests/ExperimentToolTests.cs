using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>The experiment tool's contract: strict per-action validation, the verbatim
///     ledger line shapes the finish gate parses, and the open-prediction count.</summary>
public class ExperimentToolTests
{
  private const string Key = "sci.alpha/ledger";

  private static (ExperimentTool Tool, FakeExperimentLedgerStore Store) MakeTool(string? ledger = null)
  {
    FakeExperimentLedgerStore store = ledger is null
        ? new FakeExperimentLedgerStore()
        : new FakeExperimentLedgerStore(Key, ledger);
    return (new ExperimentTool(store), store);
  }

  private static Task<ToolResult> RunAsync(ExperimentTool tool, string json) =>
      tool.ExecuteAsync(new RawToolInput("experiment", json), ct: TestContext.Current.CancellationToken);

  // ---- Group 1: happy path across every recording action ----

  [Fact]
  public async Task Record_AllSixKinds_AppendsDocumentedLinesInOrder()
  {
    (ExperimentTool? tool, FakeExperimentLedgerStore? store) = MakeTool();

    ToolResult hypothesis = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"record-hypothesis","slug":"alpha","text":"The cache is cold."}""");
    ToolResult prediction = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"record-prediction","slug":"alpha","id":1,"text":"First call is slow.","falsifiedBy":"dotnet test --filter Slow"}""");
    ToolResult verdict = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"record-verdict","slug":"alpha","id":1,"verdict":"confirmed","evidence":"dotnet test --filter Slow -> 2.1s"}""");
    ToolResult observation = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"record-observation","slug":"alpha","text":"Warm call took 12ms."}""");
    ToolResult conclusion = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"record-conclusion","slug":"alpha","text":"The cache works."}""");
    ToolResult skip = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"record-skip","slug":"alpha","text":"Skipped the load test."}""");

    Assert.False(hypothesis.IsError, hypothesis.Content);
    Assert.False(prediction.IsError, prediction.Content);
    Assert.False(verdict.IsError, verdict.Content);
    Assert.False(observation.IsError, observation.Content);
    Assert.False(conclusion.IsError, conclusion.Content);
    Assert.False(skip.IsError, skip.Content);

    Assert.Equal("[experiment] hypothesis 1 recorded in sci.alpha/ledger", hypothesis.Content);
    Assert.Equal("[experiment] prediction 1 recorded in sci.alpha/ledger", prediction.Content);
    Assert.Equal("[experiment] verdict 1 recorded in sci.alpha/ledger", verdict.Content);
    Assert.Equal("[experiment] observation 1 recorded in sci.alpha/ledger", observation.Content);
    Assert.Equal("[experiment] conclusion 1 recorded in sci.alpha/ledger", conclusion.Content);
    Assert.Equal("[experiment] skip 1 recorded in sci.alpha/ledger", skip.Content);

    Assert.Equal(6, store.AppendCalls.Count);
    Assert.All(store.AppendCalls, call => Assert.Equal(Key, call.Key));
    Assert.All(store.AppendCalls, call => Assert.Null(call.ExpectedVersion));
    Assert.Equal("hypothesis 1: The cache is cold.", store.AppendCalls[0].Text);
    Assert.Equal("prediction 1 (hypothesis 1): First call is slow. \u2014 falsified by: dotnet test --filter Slow",
        store.AppendCalls[1].Text);
    Assert.Equal("verdict 1 (prediction 1): confirmed \u2014 dotnet test --filter Slow -> 2.1s",
        store.AppendCalls[2].Text);
    Assert.Equal("observation 1: Warm call took 12ms.", store.AppendCalls[3].Text);
    Assert.Equal("conclusion 1: The cache works.", store.AppendCalls[4].Text);
    Assert.Equal("skip 1: Skipped the load test.", store.AppendCalls[5].Text);
  }

  [Fact]
  public async Task RecordHypothesis_ExplicitFreeId_UsesSuppliedId()
  {
    (ExperimentTool? tool, FakeExperimentLedgerStore? store) = MakeTool("hypothesis 1: First.");

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"record-hypothesis","slug":"alpha","id":4,"text":"Fourth."}""");

    Assert.False(result.IsError, result.Content);
    Assert.Equal("[experiment] hypothesis 4 recorded in sci.alpha/ledger", result.Content);
    Assert.Equal("hypothesis 4: Fourth.", Assert.Single(store.AppendCalls).Text);
  }

  [Fact]
  public async Task RecordHypothesis_ExplicitTakenId_InvalidParameterValue_NoAppend()
  {
    (ExperimentTool? tool, FakeExperimentLedgerStore? store) = MakeTool("hypothesis 1: First.");

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"record-hypothesis","slug":"alpha","id":1,"text":"Clash."}""");

    Assert.True(result.IsError);
    Assert.StartsWith("Error [InvalidParameterValue]:", result.Content, StringComparison.Ordinal);
    Assert.Empty(store.AppendCalls);
  }

  // ---- Group 2: a prediction must name an existing hypothesis ----

  [Fact]
  public async Task RecordPrediction_UnknownHypothesis_HypothesisNotFound_NoAppend()
  {
    (ExperimentTool? tool, FakeExperimentLedgerStore? store) = MakeTool("hypothesis 1: Only one.");

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"record-prediction","slug":"alpha","id":7,"text":"P","falsifiedBy":"cmd"}""");

    Assert.True(result.IsError);
    Assert.StartsWith("Error [HypothesisNotFound]:", result.Content, StringComparison.Ordinal);
    Assert.Empty(store.AppendCalls);
  }

  [Fact]
  public async Task RecordPrediction_WithoutFalsifiedBy_MissingParameter_NoAppend()
  {
    (ExperimentTool? tool, FakeExperimentLedgerStore? store) = MakeTool("hypothesis 1: Only one.");

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"record-prediction","slug":"alpha","id":1,"text":"P"}""");

    Assert.True(result.IsError);
    Assert.StartsWith("Error [MissingParameter]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("falsifiedBy", result.Content, StringComparison.Ordinal);
    Assert.Empty(store.AppendCalls);
  }

  [Fact]
  public async Task RecordPrediction_EmptyFalsifiedBy_InvalidParameterValue_NoAppend()
  {
    (ExperimentTool? tool, FakeExperimentLedgerStore? store) = MakeTool("hypothesis 1: Only one.");

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"record-prediction","slug":"alpha","id":1,"text":"P","falsifiedBy":"   "}""");

    Assert.True(result.IsError);
    Assert.StartsWith("Error [InvalidParameterValue]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("falsifiedBy", result.Content, StringComparison.Ordinal);
    Assert.Empty(store.AppendCalls);
  }

  // ---- Group 3: verdicts ----

  [Fact]
  public async Task RecordVerdict_UnknownPrediction_PredictionNotFound_NoAppend()
  {
    (ExperimentTool? tool, FakeExperimentLedgerStore? store) = MakeTool(
        "hypothesis 1: H\nprediction 1 (hypothesis 1): P \u2014 falsified by: cmd");

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"record-verdict","slug":"alpha","id":9,"verdict":"confirmed","evidence":"ev"}""");

    Assert.True(result.IsError);
    Assert.StartsWith("Error [PredictionNotFound]:", result.Content, StringComparison.Ordinal);
    Assert.Empty(store.AppendCalls);
  }

  [Fact]
  public async Task RecordVerdict_InvalidOutcome_InvalidParameterValue_NoAppend()
  {
    (ExperimentTool? tool, FakeExperimentLedgerStore? store) = MakeTool(
        "hypothesis 1: H\nprediction 1 (hypothesis 1): P \u2014 falsified by: cmd");

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"record-verdict","slug":"alpha","id":1,"verdict":"maybe","evidence":"ev"}""");

    Assert.True(result.IsError);
    Assert.StartsWith("Error [InvalidParameterValue]:", result.Content, StringComparison.Ordinal);
    Assert.Empty(store.AppendCalls);
  }

  [Fact]
  public async Task RecordVerdict_AlreadyJudged_PredictionAlreadyJudged_NoAppend()
  {
    (ExperimentTool? tool, FakeExperimentLedgerStore? store) = MakeTool(
        "hypothesis 1: H\nprediction 1 (hypothesis 1): P \u2014 falsified by: cmd\nverdict 1 (prediction 1): confirmed \u2014 ev");

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"record-verdict","slug":"alpha","id":1,"verdict":"refuted","evidence":"ev2"}""");

    Assert.True(result.IsError);
    Assert.StartsWith("Error [PredictionAlreadyJudged]:", result.Content, StringComparison.Ordinal);
    Assert.Empty(store.AppendCalls);
  }

  [Fact]
  public async Task RecordVerdict_ConfirmedWithoutEvidence_MissingParameter_NoAppend()
  {
    (ExperimentTool? tool, FakeExperimentLedgerStore? store) = MakeTool(
        "hypothesis 1: H\nprediction 1 (hypothesis 1): P \u2014 falsified by: cmd");

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"record-verdict","slug":"alpha","id":1,"verdict":"confirmed"}""");

    Assert.True(result.IsError);
    Assert.StartsWith("Error [MissingParameter]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("evidence", result.Content, StringComparison.Ordinal);
    Assert.Empty(store.AppendCalls);
  }

  [Fact]
  public async Task RecordVerdict_RefutedWithEmptyEvidence_InvalidParameterValue_NoAppend()
  {
    (ExperimentTool? tool, FakeExperimentLedgerStore? store) = MakeTool(
        "hypothesis 1: H\nprediction 1 (hypothesis 1): P \u2014 falsified by: cmd");

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"record-verdict","slug":"alpha","id":1,"verdict":"refuted","evidence":""}""");

    Assert.True(result.IsError);
    Assert.StartsWith("Error [InvalidParameterValue]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("evidence", result.Content, StringComparison.Ordinal);
    Assert.Empty(store.AppendCalls);
  }

  [Fact]
  public async Task RecordVerdict_InconclusiveWithoutEvidence_Succeeds_NoEvidenceTail()
  {
    (ExperimentTool? tool, FakeExperimentLedgerStore? store) = MakeTool(
        "hypothesis 1: H\nprediction 1 (hypothesis 1): P \u2014 falsified by: cmd");

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"record-verdict","slug":"alpha","id":1,"verdict":"inconclusive"}""");

    Assert.False(result.IsError, result.Content);
    Assert.Equal("[experiment] verdict 1 recorded in sci.alpha/ledger", result.Content);
    string line = Assert.Single(store.AppendCalls).Text;
    Assert.Equal("verdict 1 (prediction 1): inconclusive", line);
    Assert.DoesNotContain(" \u2014 ", line, StringComparison.Ordinal);
  }

  // ---- Group 4: skips ----

  [Fact]
  public async Task RecordSkip_BareSkip_Succeeds_ClosesNothing()
  {
    (ExperimentTool? tool, FakeExperimentLedgerStore? store) = MakeTool();

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"record-skip","slug":"alpha","text":"Operator escape: time-boxed."}""");

    Assert.False(result.IsError, result.Content);
    Assert.Equal("[experiment] skip 1 recorded in sci.alpha/ledger", result.Content);
    Assert.Equal("skip 1: Operator escape: time-boxed.", Assert.Single(store.AppendCalls).Text);
  }

  [Fact]
  public async Task RecordSkip_ClosingPrediction_RendersPredictionReference()
  {
    (ExperimentTool? tool, FakeExperimentLedgerStore? store) = MakeTool(
        "hypothesis 1: H\nprediction 1 (hypothesis 1): P \u2014 falsified by: cmd\nskip 1: earlier skip");

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"record-skip","slug":"alpha","id":1,"text":"Out of budget."}""");

    Assert.False(result.IsError, result.Content);
    Assert.Equal("[experiment] skip 2 recorded in sci.alpha/ledger", result.Content);
    Assert.Equal("skip 2 (prediction 1): Out of budget.", Assert.Single(store.AppendCalls).Text);
  }

  [Fact]
  public async Task RecordSkip_UnknownPrediction_PredictionNotFound_NoAppend()
  {
    (ExperimentTool? tool, FakeExperimentLedgerStore? store) = MakeTool(
        "hypothesis 1: H\nprediction 1 (hypothesis 1): P \u2014 falsified by: cmd");

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"record-skip","slug":"alpha","id":5,"text":"Out of budget."}""");

    Assert.True(result.IsError);
    Assert.StartsWith("Error [PredictionNotFound]:", result.Content, StringComparison.Ordinal);
    Assert.Empty(store.AppendCalls);
  }

  // ---- Group 5: list ----

  [Fact]
  public async Task List_SeededLedger_ReportsRecordAndOpenPredictionCounts()
  {
    const string Ledger =
        "hypothesis 1: H1\n" +
        "hypothesis 2: H2\n" +
        "prediction 1 (hypothesis 1): P1 \u2014 falsified by: cmd1\n" +
        "prediction 2 (hypothesis 1): P2 \u2014 falsified by: cmd2\n" +
        "prediction 3 (hypothesis 2): P3 \u2014 falsified by: cmd3\n" +
        "verdict 1 (prediction 1): confirmed \u2014 evidence one\n" +
        "skip 1 (prediction 2): ran out of time";
    (ExperimentTool? tool, FakeExperimentLedgerStore? store) = MakeTool(Ledger);

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"list","slug":"alpha"}""");

    Assert.False(result.IsError, result.Content);
    Assert.Equal(
        "[experiment] sci.alpha/ledger: 7 record(s), 1 open prediction(s)\n" + Ledger,
        result.Content);
    Assert.Empty(store.AppendCalls);
  }

  [Fact]
  public async Task List_EmptyLedger_PrintsEmptyForm()
  {
    (ExperimentTool? tool, FakeExperimentLedgerStore _) = MakeTool();

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"list","slug":"alpha"}""");

    Assert.False(result.IsError, result.Content);
    Assert.Equal("[experiment] sci.alpha/ledger: empty", result.Content);
  }

  [Fact]
  public async Task List_UnrecognizedLines_CountTowardRecords_ButNotOpenPredictions()
  {
    const string Ledger =
        "some prose the tool did not write\n" +
        "hypothesis 1: H\n" +
        "prediction 1 (hypothesis 1): P \u2014 falsified by: cmd\n" +
        "another prose line";
    (ExperimentTool? tool, FakeExperimentLedgerStore _) = MakeTool(Ledger);

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"list","slug":"alpha"}""");

    Assert.False(result.IsError, result.Content);
    Assert.StartsWith("[experiment] sci.alpha/ledger: 4 record(s), 1 open prediction(s)\n",
        result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task List_WithoutSlug_MissingParameter()
  {
    (ExperimentTool? tool, FakeExperimentLedgerStore _) = MakeTool();

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"list"}""");

    Assert.True(result.IsError);
    Assert.StartsWith("Error [MissingParameter]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("slug", result.Content, StringComparison.Ordinal);
  }

  // ---- Group 6: strict input validation ----

  [Fact]
  public async Task RecordHypothesis_UnknownParameter_UnknownParameter()
  {
    (ExperimentTool? tool, FakeExperimentLedgerStore? store) = MakeTool();

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"record-hypothesis","slug":"alpha","text":"T","bogus":1}""");

    Assert.True(result.IsError);
    Assert.StartsWith("Error [UnknownParameter]:", result.Content, StringComparison.Ordinal);
    Assert.Empty(store.AppendCalls);
  }

  [Fact]
  public async Task RecordHypothesis_EvidenceNotAccepted_InvalidParameterValue()
  {
    (ExperimentTool? tool, FakeExperimentLedgerStore? store) = MakeTool();

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"record-hypothesis","slug":"alpha","text":"T","evidence":"ev"}""");

    Assert.True(result.IsError);
    Assert.StartsWith("Error [InvalidParameterValue]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("evidence", result.Content, StringComparison.Ordinal);
    Assert.Empty(store.AppendCalls);
  }

  [Fact]
  public async Task RecordHypothesis_NonIntegerId_InvalidParameterType()
  {
    (ExperimentTool? tool, FakeExperimentLedgerStore? store) = MakeTool();

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"record-hypothesis","slug":"alpha","id":"1","text":"T"}""");

    Assert.True(result.IsError);
    Assert.StartsWith("Error [InvalidParameterType]:", result.Content, StringComparison.Ordinal);
    Assert.Empty(store.AppendCalls);
  }

  [Fact]
  public async Task RecordHypothesis_MultiLineText_InvalidParameterValue()
  {
    (ExperimentTool? tool, FakeExperimentLedgerStore? store) = MakeTool();

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"record-hypothesis","slug":"alpha","text":"line one\nline two"}""");

    Assert.True(result.IsError);
    Assert.StartsWith("Error [InvalidParameterValue]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("text", result.Content, StringComparison.Ordinal);
    Assert.Empty(store.AppendCalls);
  }

  [Fact]
  public async Task RecordHypothesis_SlugWithSlash_InvalidParameterValue()
  {
    (ExperimentTool? tool, FakeExperimentLedgerStore? store) = MakeTool();

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"record-hypothesis","slug":"alpha/beta","text":"T"}""");

    Assert.True(result.IsError);
    Assert.StartsWith("Error [InvalidParameterValue]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("slug", result.Content, StringComparison.Ordinal);
    Assert.Empty(store.AppendCalls);
  }

  [Fact]
  public async Task RecordHypothesis_SlugWithWhitespace_InvalidParameterValue()
  {
    (ExperimentTool? tool, FakeExperimentLedgerStore? store) = MakeTool();

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"record-hypothesis","slug":"alpha beta","text":"T"}""");

    Assert.True(result.IsError);
    Assert.StartsWith("Error [InvalidParameterValue]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("slug", result.Content, StringComparison.Ordinal);
    Assert.Empty(store.AppendCalls);
  }

  [Fact]
  public async Task RecordHypothesis_UnknownAction_InvalidParameterValue()
  {
    (ExperimentTool? tool, FakeExperimentLedgerStore? store) = MakeTool();

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"record-guess","slug":"alpha","text":"T"}""");

    Assert.True(result.IsError);
    Assert.StartsWith("Error [InvalidParameterValue]:", result.Content, StringComparison.Ordinal);
    Assert.Empty(store.AppendCalls);
  }

  // ---- Group 7: store failures pass through ----

  [Fact]
  public async Task RecordHypothesis_StoreConflict_PassesVersionConflictThrough()
  {
    (ExperimentTool? tool, FakeExperimentLedgerStore? store) = MakeTool();
    store.AppendResultOverride = Result.Failure<int>(new DomainError("VersionConflict",
        "Version conflict for 'sci.alpha/ledger': current version is 3."));

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"record-hypothesis","slug":"alpha","text":"T"}""");

    Assert.True(result.IsError);
    Assert.StartsWith("Error [VersionConflict]:", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task List_UnreadableLedger_FailsClosedWithStoreError()
  {
    (ExperimentTool? tool, FakeExperimentLedgerStore? store) = MakeTool();
    store.ReadResultOverride = Result.Failure<string>(new DomainError("StorageUnavailable",
        "The state store is unavailable."));

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"list","slug":"alpha"}""");

    Assert.True(result.IsError);
    Assert.StartsWith("Error [StorageUnavailable]:", result.Content, StringComparison.Ordinal);
    Assert.Empty(store.AppendCalls);
  }
  // ---- Group 8: a reference group's kind word must match the record that references it ----

  [Fact]
  public async Task List_VerdictReferencingHypothesis_IsUnrecognized_PredictionStaysOpen()
  {
    const string Ledger =
        "hypothesis 1: H\n" +
        "prediction 1 (hypothesis 1): P \u2014 falsified by: cmd\n" +
        "verdict 1 (hypothesis 1): confirmed \u2014 ev";
    (ExperimentTool? tool, FakeExperimentLedgerStore _) = MakeTool(Ledger);

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"list","slug":"alpha"}""");

    Assert.False(result.IsError, result.Content);
    Assert.StartsWith("[experiment] sci.alpha/ledger: 3 record(s), 1 open prediction(s)\n",
        result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task List_SkipWithUnknownKindWord_IsUnrecognized_PredictionStaysOpen()
  {
    const string Ledger =
        "hypothesis 1: H\n" +
        "prediction 1 (hypothesis 1): P \u2014 falsified by: cmd\n" +
        "skip 1 (banana 1): reason";
    (ExperimentTool? tool, FakeExperimentLedgerStore _) = MakeTool(Ledger);

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"list","slug":"alpha"}""");

    Assert.False(result.IsError, result.Content);
    Assert.StartsWith("[experiment] sci.alpha/ledger: 3 record(s), 1 open prediction(s)\n",
        result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task List_PredictionReferencingPrediction_IsUnrecognized_NoPredictionCreated()
  {
    const string Ledger =
        "hypothesis 1: H\n" +
        "prediction 1 (prediction 1): x \u2014 falsified by: y";
    (ExperimentTool? tool, FakeExperimentLedgerStore _) = MakeTool(Ledger);

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"list","slug":"alpha"}""");

    Assert.False(result.IsError, result.Content);
    // The malformed line is unrecognized, so no prediction exists and none is open.
    Assert.StartsWith("[experiment] sci.alpha/ledger: 2 record(s), 0 open prediction(s)\n",
        result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task List_CorrectlyWordedReferences_StillParseAndStillClose()
  {
    const string Ledger =
        "hypothesis 1: H\n" +
        "prediction 1 (hypothesis 1): P1 \u2014 falsified by: cmd\n" +
        "prediction 2 (hypothesis 1): P2 \u2014 falsified by: cmd\n" +
        "verdict 1 (prediction 1): confirmed \u2014 ev\n" +
        "skip 1 (prediction 2): reason";
    (ExperimentTool? tool, FakeExperimentLedgerStore? store) = MakeTool(Ledger);

    ToolResult listed = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"list","slug":"alpha"}""");

    Assert.False(listed.IsError, listed.Content);
    Assert.StartsWith("[experiment] sci.alpha/ledger: 5 record(s), 0 open prediction(s)\n",
        listed.Content, StringComparison.Ordinal);

    // Both predictions are recognized and closed, so a further verdict is refused.
    ToolResult judged = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"record-verdict","slug":"alpha","id":1,"verdict":"refuted","evidence":"ev2"}""");

    Assert.True(judged.IsError);
    Assert.StartsWith("Error [PredictionAlreadyJudged]:", judged.Content, StringComparison.Ordinal);
    Assert.Empty(store.AppendCalls);
  }

  // ---- Group 9: list rejects every recording-only parameter ----

  [Fact]
  public async Task List_RecordingOnlyParameters_AreRejected_NoAppend()
  {
    (string Name, string Json)[] rejected =
    [
        ("id", /*lang=json,strict*/ """{"timeoutSeconds":120,"action":"list","slug":"alpha","id":1}"""),
        ("text", /*lang=json,strict*/ """{"timeoutSeconds":120,"action":"list","slug":"alpha","text":"T"}"""),
        ("falsifiedBy", /*lang=json,strict*/ """{"timeoutSeconds":120,"action":"list","slug":"alpha","falsifiedBy":"cmd"}"""),
        ("evidence", /*lang=json,strict*/ """{"timeoutSeconds":120,"action":"list","slug":"alpha","evidence":"ev"}"""),
        ("verdict", /*lang=json,strict*/ """{"timeoutSeconds":120,"action":"list","slug":"alpha","verdict":"confirmed"}"""),
    ];

    foreach ((string name, string json) in rejected)
    {
      (ExperimentTool? tool, FakeExperimentLedgerStore? store) = MakeTool();

      ToolResult result = await RunAsync(tool, json);

      Assert.True(result.IsError, name);
      Assert.StartsWith("Error [InvalidParameterValue]:", result.Content, StringComparison.Ordinal);
      Assert.Contains($"'{name}' is not accepted for action 'list'", result.Content,
          StringComparison.Ordinal);
      Assert.Empty(store.AppendCalls);
    }
  }

  // ---- Group 10: blank lines are never records ----

  [Fact]
  public async Task List_BlankInteriorLines_AreNotRecords()
  {
    const string Ledger =
        "hypothesis 1: H\n" +
        "\n" +
        "prediction 1 (hypothesis 1): P \u2014 falsified by: cmd\n" +
        "   \n" +
        "observation 1: O";
    (ExperimentTool? tool, FakeExperimentLedgerStore _) = MakeTool(Ledger);

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"list","slug":"alpha"}""");

    Assert.False(result.IsError, result.Content);
    Assert.Equal(
        "[experiment] sci.alpha/ledger: 3 record(s), 1 open prediction(s)\n" +
        "hypothesis 1: H\n" +
        "prediction 1 (hypothesis 1): P \u2014 falsified by: cmd\n" +
        "observation 1: O",
        result.Content);
  }


  /// <summary>In-memory append-only ledger mirroring the composition adapter's
  ///     semantics: a missing key reads KeyNotFound, an append with a null
  ///     expectedVersion creates the key or appends a line. Scriptable overrides
  ///     force failure results.</summary>
  // ---- Group 5b: list cap and summary (issue #144) ----

  [Fact]
  public async Task List_OverTheCap_EmitsTheCapPlusATruncationMarker()
  {
    // 60 hypotheses: far past the 50-record cap. The header must stay complete
    // (B4) and the marker must name the dropped count (B1/B3).
    string ledger = string.Join("\n", Enumerable.Range(1, 60).Select(i => $"hypothesis {i}: H{i}"));
    (ExperimentTool? tool, _) = MakeTool(ledger);

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"list","slug":"alpha"}""");

    Assert.False(result.IsError, result.Content);
    string[] lines = result.Content.Split('\n');
    Assert.Equal("[experiment] sci.alpha/ledger: 60 record(s), 0 open prediction(s)", lines[0]);
    Assert.Equal(52, lines.Length); // header + 50 capped records + marker
    string marker = lines[^1];
    Assert.Contains("10 more record(s) not shown", marker, StringComparison.Ordinal);
    Assert.Contains("summary", marker, StringComparison.Ordinal);
  }

  [Fact]
  public async Task List_AtTheCap_NoMarker()
  {
    string ledger = string.Join("\n", Enumerable.Range(1, 50).Select(i => $"hypothesis {i}: H{i}"));
    (ExperimentTool? tool, _) = MakeTool(ledger);

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"list","slug":"alpha"}""");

    Assert.False(result.IsError, result.Content);
    Assert.Equal(51, result.Content.Split('\n').Length);
    Assert.DoesNotContain("not shown", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task List_HeaderCountsComplete_EvenWhenBodiesTruncated()
  {
    // B4: 200 records with 3 open predictions - the header reports 3, not a
    // count derived from the capped echo.
    string[] open =
    [
        "prediction 1 (hypothesis 1): P1 \u2014 falsified by: cmd1",
        "prediction 2 (hypothesis 1): P2 \u2014 falsified by: cmd2",
        "prediction 3 (hypothesis 2): P3 \u2014 falsified by: cmd3",
    ];
    string[] closed =
    [
        "verdict 1 (prediction 1): confirmed \u2014 evidence",
        "verdict 2 (prediction 2): refuted \u2014 evidence",
        "skip 3 (prediction 3): done",
    ];
    IEnumerable<string> filler = Enumerable.Range(4, 194).Select(i => $"hypothesis {i}: H{i}");
    string ledger = string.Join("\n", open.Concat(closed).Concat(filler));
    (ExperimentTool? tool, _) = MakeTool(ledger);

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"list","slug":"alpha"}""");

    Assert.False(result.IsError, result.Content);
    Assert.StartsWith("[experiment] sci.alpha/ledger: 200 record(s), 0 open prediction(s)",
        result.Content, StringComparison.Ordinal);
    Assert.Contains("not shown", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Summary_ReturnsHeaderOnly_NoRecordBodies()
  {
    const string Ledger =
        "hypothesis 1: H1\n" +
        "prediction 1 (hypothesis 1): P1 \u2014 falsified by: cmd1\n" +
        "verdict 1 (prediction 1): confirmed \u2014 evidence one";
    (ExperimentTool? tool, FakeExperimentLedgerStore? store) = MakeTool(Ledger);

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"summary","slug":"alpha"}""");

    Assert.False(result.IsError, result.Content);
    Assert.Equal("[experiment] sci.alpha/ledger: 3 record(s), 0 open prediction(s)", result.Content);
    Assert.Empty(store.AppendCalls);
  }

  [Fact]
  public async Task Summary_EmptyLedger_PrintsEmptyForm()
  {
    (ExperimentTool? tool, _) = MakeTool();

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"summary","slug":"alpha"}""");

    Assert.False(result.IsError, result.Content);
    Assert.Equal("[experiment] sci.alpha/ledger: empty", result.Content);
  }

  [Fact]
  public async Task Summary_RecordingOnlyParameters_AreRejected()
  {
    (ExperimentTool? tool, _) = MakeTool();

    ToolResult result = await RunAsync(tool,
        /*lang=json,strict*/
        """{"timeoutSeconds":120,"action":"summary","slug":"alpha","text":"x"}""");

    Assert.True(result.IsError);
    Assert.StartsWith("Error [InvalidParameterValue]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("'text' is not accepted", result.Content, StringComparison.Ordinal);
  }

  private sealed class FakeExperimentLedgerStore : IExperimentLedgerStore
  {
    private readonly Dictionary<string, string> _keys = [];

    public FakeExperimentLedgerStore() { }

    public FakeExperimentLedgerStore(string key, string value) => _keys[key] = value;

    public Result<int>? AppendResultOverride { get; set; }

    public Result<string>? ReadResultOverride { get; set; }

    public List<string> ReadCalls { get; } = [];

    public List<(string Key, string Text, int? ExpectedVersion)> AppendCalls { get; } = [];

    private Result<string> Stored(string key) =>
        _keys.TryGetValue(key, out string? value)
          ? Result.Success(value)
          : Result.Failure<string>(new DomainError("KeyNotFound", $"'{key}' does not exist."));

    public Task<Result<string>> ReadAsync(string key, CancellationToken ct = default)
    {
      ReadCalls.Add(key);
      return Task.FromResult(ReadResultOverride ?? Stored(key));
    }

    public Task<Result<int>> AppendAsync(string key, string text, int? expectedVersion,
        CancellationToken ct = default)
    {
      AppendCalls.Add((key, text, expectedVersion));
      if (AppendResultOverride is { } appendOverride)
      {
        return Task.FromResult(appendOverride);
      }

      _keys[key] = _keys.TryGetValue(key, out string? existing) ? existing + "\n" + text : text;
      return Task.FromResult(Result.Success(_keys[key].Length));
    }
  }
}
