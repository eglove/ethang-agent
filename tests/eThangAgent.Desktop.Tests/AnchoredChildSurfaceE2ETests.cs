namespace eThangAgent.Desktop.Tests;

/// <summary>
/// End-to-end proof of capability-surface re-rooting: a REAL spawned anchored child
/// reads a file AT ITS ANCHOR through the capability surface (the merged anchored
/// factory) and is refused reading outside it — through the real composition, real
/// spawn anchor validation, and the real in-process child runtime, against the mock
/// provider playing both parent and child via model-keyed scripting.
/// </summary>
[Collection("Desktop E2E")]
public class AnchoredChildSurfaceE2ETests
{
  private static string RawCompletion(string content) =>
      System.Text.Json.JsonSerializer.Serialize(
          new { choices = new[] { new { message = new { content } } } });

  [Fact]
  public async Task AnchoredChild_ExecSurfaceResolvesAtAnchor_AndRefusesEscape()
  {
    DirectoryInfo ws = Directory.CreateTempSubdirectory("ethang-e2e-anchor");
    try
    {
      string anchor = Path.Combine(ws.FullName, "anchored");
      _ = Directory.CreateDirectory(anchor);
      const string probeName = "probe.txt";
      await File.WriteAllTextAsync(Path.Combine(anchor, probeName), "anchor-surface-probe",
          TestContext.Current.CancellationToken);

      using E2E.HostHarness host = new();
      _ = await host.StartAsync(workspaceRoot: ws.FullName);

      // Child program: read the probe AT the anchor (relative — the anchored surface
      // resolves it), then attempt to climb out of the anchor. Both results flow back
      // so the transcript carries the evidence. Built by plain concatenation.
      string childProgram =
          "var hit = Tools.Invoke(\"read\", new { timeoutSeconds = 60, path = \"" + probeName + "\", startLine = 1, endLine = 1 });"
          + " var miss = Tools.Invoke(\"read\", new { timeoutSeconds = 60, path = \"../../../../" + probeName + "\", startLine = 1, endLine = 1 });"
          + " return hit + \"\\n\" + miss;";

      // Parent script, keyed by the session model: spawn anchored, then the bounded
      // poll-then-result fetch (COPIED VERBATIM from NestedSpawn_ChildRunsAndReports —
      // see the bounded-poll comment there), then the final text.
      const string pollThenResult = """
              var deadline = System.DateTime.UtcNow.AddSeconds(30);
              var status = Tools.Invoke("agent.status", new { timeoutSeconds = 60, id = "{{child_id}}" });
              while (!status.Contains("status=completed") && !status.Contains("status=failed"))
              {
                  if (System.DateTime.UtcNow > deadline)
                      return "poll-timeout; last status: " + status;
                  await System.Threading.Tasks.Task.Delay(50);
                  status = Tools.Invoke("agent.status", new { timeoutSeconds = 60, id = "{{child_id}}" });
              }
              return Tools.Invoke("agent.result", new { timeoutSeconds = 60, id = "{{child_id}}" });
              """;
      _ = host.Mock.ReturnsForModel(E2E.SessionModel,
          E2E.ExecToolCall("parent_call_1", E2E.ExecProgram(
              "var spawned = Tools.Invoke(\"agent.spawn\", new { timeoutSeconds = 60, taskPrompt = \"Say child report done and nothing else.\", model = \"mock/sub-model\", label = \"e2e-anchor\", workspaceRoot = \"" + anchor.Replace('\\', '/') + "\" }); return spawned;")),
          E2E.ExecToolCall("parent_call_2", E2E.ExecProgram(pollThenResult)),
          RawCompletion("anchored child done"));

      // Child script, keyed by the per-spawn model: the anchored-surface probe, then
      // the final report.
      _ = host.Mock.ReturnsForModel("mock/sub-model",
          E2E.ExecToolCall("child_call_1", E2E.ExecProgram(childProgram)),
          RawCompletion("child report done"));

      // Bounded like every externally-settled await in the suite: a stuck turn must
      // fail the test, not hold the runner hostage.
      await host.Vm.RunTurnAsync("delegate an anchored subtask")
          .WaitAsync(TimeSpan.FromSeconds(120), TestContext.Current.CancellationToken);

      // (a) The anchored read SAW the probe file: the capability surface resolved at
      //     the anchor, not the session root.
      string hit = E2E.FindToolMessageContaining(host.Mock.RequestBodies, "anchor-surface-probe");
      Assert.Contains("anchor-surface-probe", hit, StringComparison.Ordinal);

      // (b) The escape read was REFUSED by the anchored resolver before any IO.
      _ = E2E.FindToolMessageContaining(host.Mock.RequestBodies, "Error [PathOutsideWorkspace]");

      // (c) The child's report reached the parent through agent.result.
      Assert.Contains("child report done",
          E2E.FindToolMessageContaining(host.Mock.RequestBodies, "child report done"),
          StringComparison.Ordinal);
    }
    finally
    {
      // The harness disposes itself via using (temp db cleanup is its own).
      ws.Delete(true);
    }
  }
}
