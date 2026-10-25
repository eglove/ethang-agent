using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

/// <summary>
/// Structured-plan E2E: a model-authored exec program drives the full plan lifecycle
/// (create -> show -> set-status -> frozen-mutation rejected) through capability
/// dispatch, the real composition, and a real SqlitePlanStore, against the mock provider.
/// </summary>
[Collection("Desktop E2E")]
public class PlanCapabilityE2ETests
{
  private static string RawCompletion(string content) => E2E.RawCompletion(content);

  [Fact]
  public async Task Agent_DrivesPlanLifecycle_ThroughCapabilityDispatch()
  {
    using E2E.HostHarness host = new();
    _ = await host.StartAsync();

    string program = """
            var linked = Tools.Invoke("todo", new { timeoutSeconds = 30, action = "Add", description = "linked" });
            var keeper = Tools.Invoke("todo", new { timeoutSeconds = 30, action = "Add", description = "keeper" });
            var created = Tools.Invoke("plan.create", new { timeoutSeconds = 60,
                title = "Ship the thing", goal = "Make it good",
                steps = new[] { new { title = "build", todoId = 1 }, new { title = "test", todoId = 2 } } });
            if (!created.StartsWith("[plan] created #")) return "BAD CREATE: " + created;
            var extra = Tools.Invoke("todo", new { timeoutSeconds = 30, action = "Add", description = "extra" });
            var shown = Tools.Invoke("plan.show", new { timeoutSeconds = 60, id = 1 });
            // Completion demands a consistent plan (issue #271): finishing one step and
            // SKIPPING the other is the whole point of the Skipped status.
            var refused = Tools.Invoke("plan.set-status", new { timeoutSeconds = 60,
                id = 1, status = "Completed" });
            var step1 = Tools.Invoke("plan.update-step", new { timeoutSeconds = 60,
                id = 1, position = 1, status = "Done" });
            var step2 = Tools.Invoke("plan.update-step", new { timeoutSeconds = 60,
                id = 1, position = 2, status = "Skipped" });
            var done = Tools.Invoke("plan.set-status", new { timeoutSeconds = 60,
                id = 1, status = "Completed" });
            var frozen = Tools.Invoke("plan.add-step", new { timeoutSeconds = 60,
                id = 1, title = "late" });
            var todos = Tools.Invoke("todo", new { timeoutSeconds = 30, action = "List" });
            return linked + "|" + extra + "|" + created + "|" + (shown.Contains("session ") && !shown.Contains("session ]") ? "STAMPED" : "NO-SESSION") + "|" + refused + "|" + step1 + "|" + step2 + "|" + done + "|" + frozen + "|" + todos;
            """;
    _ = host.Mock.Returns(E2E.ExecToolCall("call_1", E2E.ExecProgram(program)));
    _ = host.Mock.Returns(RawCompletion("plan lifecycle verified"));

    await host.Vm.RunTurnAsync("run the plan lifecycle");

    Assert.True(host.Mock.RequestBodies.Count >= 2,
        $"expected at least 2 scripted requests, got {host.Mock.RequestBodies.Count}");
    string assistant = string.Join("", host.Vm.Transcript.Entries
        .OfType<AssistantTextEntry>().Select(a => a.Text));
    Assert.Contains("plan lifecycle verified", assistant, StringComparison.OrdinalIgnoreCase);
    string toolContent = E2E.GetLastToolMessage(host.Mock.RequestBodies[1]);
    Assert.Contains("[plan] created #", toolContent, StringComparison.Ordinal);
    Assert.Contains("STAMPED", toolContent, StringComparison.Ordinal);
    // The unearned completion is refused, naming what is unfinished; the earned one lands.
    Assert.Contains("Error [InvalidTransition]:", toolContent, StringComparison.Ordinal);
    Assert.Contains("2 (Pending)", toolContent, StringComparison.Ordinal);
    Assert.Contains("[plan] updated step 1 in #1", toolContent, StringComparison.Ordinal);
    Assert.Contains("[plan] updated step 2 in #1", toolContent, StringComparison.Ordinal);
    Assert.Contains("[plan] #1 is now Completed", toolContent, StringComparison.Ordinal);
    Assert.Contains("[plan] #1 cleaned 2 linked todo(s)", toolContent, StringComparison.Ordinal);
    Assert.Contains("InvalidTransition", toolContent, StringComparison.Ordinal);
    Assert.Contains("[todo: 1 open / 1 total]", toolContent, StringComparison.Ordinal);
    Assert.Contains("#3 [Pending] extra", toolContent, StringComparison.Ordinal);
  }
}
