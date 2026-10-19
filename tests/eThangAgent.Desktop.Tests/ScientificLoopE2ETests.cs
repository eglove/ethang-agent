using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

/// <summary>
/// Scientific-loop E2E: a model-authored exec program drives the `experiment` tool through
/// capability dispatch, the real composition, and a real SqliteStateStore, against the mock
/// provider. The assertions target the finish gate's ONLY decision input — the ledger's
/// open-prediction count, which must move 1 -> 0 when the verdict lands — plus the negative
/// direction that makes the gate meaningful: a verdict against a nonexistent prediction id is
/// refused, so the tool cannot manufacture a closing record.
/// </summary>
[Collection("Desktop E2E")]
public class ScientificLoopE2ETests
{
  private const string Ledger = "sci.e2e-loop/ledger";

  private static string RawCompletion(string content) => E2E.RawCompletion(content);

  [Fact]
  public async Task Agent_DrivesScientificLoop_AndFinishGateBlocksOnOpenPrediction()
  {
    using E2E.HostHarness host = new();
    _ = await host.StartAsync();

    // Step 1: one hypothesis, one prediction naming the check that would refute it, then
    // the listing the finish gate reads. The prediction is OPEN here, so the gate blocks.
    string openProgram = """
        var hypothesis = Tools.Invoke("experiment", new { timeoutSeconds = 60, action = "record-hypothesis", slug = "e2e-loop", text = "The finish gate reads the ledger's open-prediction count." });
        var prediction = Tools.Invoke("experiment", new { timeoutSeconds = 60, action = "record-prediction", slug = "e2e-loop", id = 1, text = "A recorded verdict closes the prediction.", falsifiedBy = "experiment list still reports 1 open prediction(s)" });
        var open = Tools.Invoke("experiment", new { timeoutSeconds = 60, action = "list", slug = "e2e-loop" });
        return hypothesis + "|" + prediction + "|" + open;
        """;

    // Step 2: the verdict, the listing again (the gate now passes), then a verdict against
    // an id that does not exist — refused, and the ledger gains nothing.
    string closeProgram = """
        var verdict = Tools.Invoke("experiment", new { timeoutSeconds = 60, action = "record-verdict", slug = "e2e-loop", id = 1, verdict = "confirmed", evidence = "dotnet test --filter-class ScientificLoopE2ETests -> passed" });
        var closed = Tools.Invoke("experiment", new { timeoutSeconds = 60, action = "list", slug = "e2e-loop" });
        var refused = Tools.Invoke("experiment", new { timeoutSeconds = 60, action = "record-verdict", slug = "e2e-loop", id = 99, verdict = "refuted", evidence = "nothing to refute" });
        var after = Tools.Invoke("experiment", new { timeoutSeconds = 60, action = "list", slug = "e2e-loop" });
        return verdict + "|" + closed + "|" + refused + "|" + after;
        """;

    _ = host.Mock.Returns(E2E.ExecToolCall("call_1", E2E.ExecProgram(openProgram)));
    _ = host.Mock.Returns(E2E.ExecToolCall("call_2", E2E.ExecProgram(closeProgram)));
    _ = host.Mock.Returns(RawCompletion("scientific loop verified"));

    await host.Vm.RunTurnAsync("run the scientific loop and check the ledger gate");

    Assert.True(host.Mock.RequestBodies.Count >= 3,
        $"expected at least 3 scripted requests, got {host.Mock.RequestBodies.Count}");

    string openContent = E2E.GetLastToolMessage(host.Mock.RequestBodies[1]);
    Assert.Contains($"[experiment] hypothesis 1 recorded in {Ledger}", openContent, StringComparison.Ordinal);
    Assert.Contains($"[experiment] prediction 1 recorded in {Ledger}", openContent, StringComparison.Ordinal);
    // The gate's predicate: one prediction open, so finishing-a-development-branch blocks.
    Assert.Contains($"[experiment] {Ledger}: 2 record(s), 1 open prediction(s)", openContent, StringComparison.Ordinal);
    // The listing carries the prediction line verbatim — exactly what the gate shows the user.
    Assert.Contains("prediction 1 (hypothesis 1): A recorded verdict closes the prediction.",
        openContent, StringComparison.Ordinal);

    string closedContent = E2E.GetLastToolMessage(host.Mock.RequestBodies[2]);
    // The exec program joins four results with '|'. Splitting pins WHICH segment satisfies
    // each assertion, so a passing check can never ride a neighbouring segment's text.
    string[] segments = closedContent.Split('|');
    Assert.Equal(4, segments.Length);
    string verdictSegment = segments[0];
    string closedSegment = segments[1];
    string refusedSegment = segments[2];
    string afterSegment = segments[3];

    Assert.Contains($"[experiment] verdict 1 recorded in {Ledger}", verdictSegment, StringComparison.Ordinal);
    Assert.Contains("verdict 1 (prediction 1): confirmed", closedSegment, StringComparison.Ordinal);
    // The predicate moved: no prediction is open, so the gate passes.
    Assert.Contains($"[experiment] {Ledger}: 3 record(s), 0 open prediction(s)", closedSegment, StringComparison.Ordinal);

    // A verdict against a nonexistent prediction is refused, and names its reason.
    Assert.Contains("Error [PredictionNotFound]", refusedSegment, StringComparison.Ordinal);
    // The unchanged-ledger half, pinned to the WHOLE post-refusal listing rather than to a
    // record count: the listing is rendered from the ledger, so a refusal that appends
    // anything at all — one record or ten — makes the after-segment differ from the
    // closed-segment. Byte equality catches every leak size; a magic count caught only one.
    Assert.Contains($"[experiment] {Ledger}: 3 record(s), 0 open prediction(s)", afterSegment, StringComparison.Ordinal);
    Assert.Equal(closedSegment, afterSegment);

    string assistant = string.Join("", host.Vm.Transcript.Entries
        .OfType<AssistantTextEntry>().Select(a => a.Text));
    Assert.Contains("scientific loop verified", assistant, StringComparison.OrdinalIgnoreCase);
  }
}
