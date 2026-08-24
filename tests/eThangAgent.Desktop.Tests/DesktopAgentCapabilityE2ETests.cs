using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

/// <summary>
/// Headless end-to-end coverage ported from the retired piped-CLI suite:
/// durable-state discipline and the todo/reserved-namespace boundary through
/// the real composition against the mock provider.
/// </summary>
[Collection("Desktop E2E")]
public class DesktopAgentCapabilityE2ETests
{
    private static string RawCompletion(string content) =>
        System.Text.Json.JsonSerializer.Serialize(
            new { choices = new[] { new { message = new { content } } } });

    [Fact]
    public async Task StateDisciplineLoop_Certifies()
    {
        using var host = new E2E.Host().Start();

        var program = """
            Tools.Invoke("state.set", new { key = "current/head", value = "done" });
            Tools.Invoke("state.transition", new { from = "coding", to = "done", summary = "work", evidence = new[] { "true" } });
            return Tools.Invoke("state.verify", new { });
            """;
        host.Mock.Returns(E2E.ExecToolCall("call_1", E2E.ExecProgram(program)));
        host.Mock.Returns(RawCompletion("certified"));

        await host.Vm.RunTurnAsync("track the work");

        var assistant = string.Join("", host.Vm.Transcript.Entries
            .OfType<AssistantTextEntry>().Select(a => a.Text));
        Assert.Contains("certified", assistant, StringComparison.OrdinalIgnoreCase);
        Assert.True(host.Mock.RequestBodies.Count >= 2);
        Assert.Contains("\"Certified\":true",
            E2E.GetLastToolMessage(host.Mock.RequestBodies[1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StateDisciplineLoop_Violated_OnFailingEvidence()
    {
        using var host = new E2E.Host().Start();

        var program = """
            Tools.Invoke("state.set", new { key = "current/head", value = "done" });
            Tools.Invoke("state.transition", new { from = "coding", to = "done", summary = "work", evidence = new[] { "throw new System.Exception(\"boom\")" } });
            return Tools.Invoke("state.verify", new { });
            """;
        host.Mock.Returns(E2E.ExecToolCall("call_1", E2E.ExecProgram(program)));
        host.Mock.Returns(RawCompletion("violated"));

        await host.Vm.RunTurnAsync("track the work");

        var assistant = string.Join("", host.Vm.Transcript.Entries
            .OfType<AssistantTextEntry>().Select(a => a.Text));
        Assert.Contains("violated", assistant, StringComparison.OrdinalIgnoreCase);
        var toolContent = E2E.GetLastToolMessage(host.Mock.RequestBodies[1]);
        Assert.Contains("\"Certified\":false", toolContent, StringComparison.Ordinal);
        Assert.Contains("\"Violated\":true", toolContent, StringComparison.Ordinal);
        Assert.Contains("boom", toolContent, StringComparison.Ordinal);
    }

    /// <summary>Boundary honesty E2E over the composed stack: the todo tool's own writes
    ///     flow through StateServiceTodoListStore → StateService → SqliteStateStore and
    ///     succeed, while model-invoked state.set/state.delete against the reserved
    ///     'todo' namespace are rejected at the capability boundary with ReservedNamespace
    ///     and leave the persisted todo document untouched.</summary>
    [Fact]
    public async Task TodoToolWritesFlow_ButModelStateWritesOnTodoNs_AreRejected()
    {
        using var host = new E2E.Host().Start();

        host.Mock.Returns(E2E.ExecToolCall("call_1", E2E.ExecProgram("Tools.Invoke(\"todo\", new { action = \"Add\", description = \"ship it\" })")));
        host.Mock.Returns(E2E.ExecToolCall("call_2", E2E.ExecProgram("Tools.Invoke(\"state.set\", new { key = \"todo/list\", value = \"hijack\" })")));
        host.Mock.Returns(E2E.ExecToolCall("call_3", E2E.ExecProgram("Tools.Invoke(\"state.delete\", new { key = \"todo/list\" })")));
        host.Mock.Returns(E2E.ExecToolCall("call_4", E2E.ExecProgram("Tools.Invoke(\"todo\", new { action = \"List\" })")));
        host.Mock.Returns(RawCompletion("done"));

        await host.Vm.RunTurnAsync("track one task, then try to write todo state directly");

        Assert.True(host.Mock.RequestBodies.Count >= 5,
            $"expected at least 5 scripted requests, got {host.Mock.RequestBodies.Count}");

        // (a) Composed flow: the todo tool's own adapter write landed in durable state.
        Assert.Contains("[todo] added #1",
            E2E.GetLastToolMessage(host.Mock.RequestBodies[1]), StringComparison.Ordinal);

        // (b) Boundary gate: model-invoked writes to the reserved namespace are rejected
        //     with ReservedNamespace, never reaching the service.
        Assert.Contains("ReservedNamespace",
            E2E.GetLastToolMessage(host.Mock.RequestBodies[2]), StringComparison.Ordinal);
        Assert.Contains("ReservedNamespace",
            E2E.GetLastToolMessage(host.Mock.RequestBodies[3]), StringComparison.Ordinal);

        // (c) The rejected foreign writes left the persisted todo document untouched.
        Assert.Contains("#1 [Pending] ship it",
            E2E.GetLastToolMessage(host.Mock.RequestBodies[4]), StringComparison.Ordinal);

        var assistant = string.Join("", host.Vm.Transcript.Entries
            .OfType<AssistantTextEntry>().Select(a => a.Text));
        Assert.Contains("done", assistant, StringComparison.OrdinalIgnoreCase);
    }
}