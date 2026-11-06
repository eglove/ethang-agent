using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

/// <summary>
/// Headless end-to-end coverage ported from the retired piped-CLI suite: durable-state
/// discipline and memory recall through the real composition against the mock provider.
/// <para>These facts drive the COMPOSED stack - host, exec bridge, capability dispatch -
/// rather than a provider in isolation, so a wiring break between the tool surface and the
/// real store fails here. The spawn facts that used to share this file went with agent.spawn
/// (issue #334); these four never depended on it.</para>
/// </summary>
[Collection("Desktop E2E")]
public class DesktopStateCapabilityE2ETests
{
  private static string RawCompletion(string content) => E2E.RawCompletion(content);

  [Fact]
  public async Task StateDisciplineLoop_Certifies()
  {
    using E2E.HostHarness host = new();
    _ = await host.StartAsync();

    string program = """
            Tools.Invoke("state.set", new { timeoutSeconds = 60, key = "current/head", value = "done" });
            Tools.Invoke("state.transition", new { timeoutSeconds = 60, from = "coding", to = "done", summary = "work", evidence = new[] { "true" } });
            return Tools.Invoke("state.verify", new { timeoutSeconds = 60 });
            """;
    _ = host.Mock.Returns(E2E.ExecToolCall("call_1", E2E.ExecProgram(program)));
    _ = host.Mock.Returns(RawCompletion("certified"));

    await host.Vm.RunTurnAsync("track the work");

    string assistant = string.Join("", host.Vm.Transcript.Entries
        .OfType<AssistantTextEntry>().Select(a => a.Text));
    Assert.Contains("certified", assistant, StringComparison.OrdinalIgnoreCase);
    Assert.True(host.Mock.RequestBodies.Count >= 2);
    Assert.Contains("\"Certified\":true",
        E2E.GetLastToolMessage(host.Mock.RequestBodies[1]), StringComparison.Ordinal);
  }

  [Fact]
  public async Task StateDisciplineLoop_Violated_OnFailingEvidence()
  {
    using E2E.HostHarness host = new();
    _ = await host.StartAsync();

    string program = """
            Tools.Invoke("state.set", new { timeoutSeconds = 60, key = "current/head", value = "done" });
            Tools.Invoke("state.transition", new { timeoutSeconds = 60, from = "coding", to = "done", summary = "work", evidence = new[] { "throw new System.Exception(\"boom\")" } });
            return Tools.Invoke("state.verify", new { timeoutSeconds = 60 });
            """;
    _ = host.Mock.Returns(E2E.ExecToolCall("call_1", E2E.ExecProgram(program)));
    _ = host.Mock.Returns(RawCompletion("violated"));

    await host.Vm.RunTurnAsync("track the work");

    string assistant = string.Join("", host.Vm.Transcript.Entries
        .OfType<AssistantTextEntry>().Select(a => a.Text));
    Assert.Contains("violated", assistant, StringComparison.OrdinalIgnoreCase);
    string toolContent = E2E.GetLastToolMessage(host.Mock.RequestBodies[1]);
    Assert.Contains("\"Certified\":false", toolContent, StringComparison.Ordinal);
    Assert.Contains("\"Violated\":true", toolContent, StringComparison.Ordinal);
    Assert.Contains("boom", toolContent, StringComparison.Ordinal);
  }

  /// <summary>Issue #310: the todo tool is retired, so the composed stack no longer binds
  ///     a 'todo' action at all - a script that calls one is told the action is unknown, and
  ///     the retired namespace is no longer reserved. Both facts are pinned here over the
  ///     real composition, because the reservation used to live at the capability boundary
  ///     and a deletion that left it behind would keep a stale row unwritable.</summary>
  [Fact]
  public async Task RetiredTodoAction_IsUnknown_AndItsNamespaceIsNoLongerReserved()
  {
    using E2E.HostHarness host = new();
    _ = await host.StartAsync();

    _ = host.Mock.Returns(E2E.ExecToolCall("call_1", E2E.ExecProgram("Tools.Invoke(\"todo\", new { timeoutSeconds = 120, action = \"Add\", description = \"ship it\" })")));
    _ = host.Mock.Returns(E2E.ExecToolCall("call_2", E2E.ExecProgram("Tools.Invoke(\"state.set\", new { timeoutSeconds = 60, key = \"todo/list\", value = \"now an ordinary key\" })")));
    _ = host.Mock.Returns(RawCompletion("done"));

    await host.Vm.RunTurnAsync("the todo tool is gone; its namespace is not special any more");

    Assert.True(host.Mock.RequestBodies.Count >= 3,
        $"expected at least 3 scripted requests, got {host.Mock.RequestBodies.Count}");

    // (a) No 'todo' capability is bound: the dispatch fails naming the action.
    string todoCall = E2E.GetLastToolMessage(host.Mock.RequestBodies[1]);
    Assert.Contains("todo", todoCall, StringComparison.Ordinal);
    Assert.True(
        todoCall.Contains("Unknown", StringComparison.OrdinalIgnoreCase)
        || todoCall.Contains("not granted", StringComparison.OrdinalIgnoreCase)
        || todoCall.Contains("Error [", StringComparison.Ordinal),
        $"expected a dispatch refusal naming 'todo', got: {todoCall}");
    Assert.DoesNotContain("[todo] added", todoCall, StringComparison.Ordinal);

    // (b) The retired namespace is an ORDINARY key now: no ReservedNamespace refusal.
    string stateCall = E2E.GetLastToolMessage(host.Mock.RequestBodies[2]);
    Assert.DoesNotContain("ReservedNamespace", stateCall, StringComparison.Ordinal);
    Assert.Contains("saved todo/list", stateCall, StringComparison.Ordinal);

    string assistant = string.Join("", host.Vm.Transcript.Entries
        .OfType<AssistantTextEntry>().Select(a => a.Text));
    Assert.Contains("done", assistant, StringComparison.OrdinalIgnoreCase);
  }
}
