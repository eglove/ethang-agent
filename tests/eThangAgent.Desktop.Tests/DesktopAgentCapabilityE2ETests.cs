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
        using var host = await new E2E.Host().StartAsync();

        var program = """
            Tools.Invoke("state.set", new { timeoutSeconds = 60, key = "current/head", value = "done" });
            Tools.Invoke("state.transition", new { timeoutSeconds = 60, from = "coding", to = "done", summary = "work", evidence = new[] { "true" } });
            return Tools.Invoke("state.verify", new { timeoutSeconds = 60 });
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
        using var host = await new E2E.Host().StartAsync();

        var program = """
            Tools.Invoke("state.set", new { timeoutSeconds = 60, key = "current/head", value = "done" });
            Tools.Invoke("state.transition", new { timeoutSeconds = 60, from = "coding", to = "done", summary = "work", evidence = new[] { "throw new System.Exception(\"boom\")" } });
            return Tools.Invoke("state.verify", new { timeoutSeconds = 60 });
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
        using var host = await new E2E.Host().StartAsync();

        host.Mock.Returns(E2E.ExecToolCall("call_1", E2E.ExecProgram("Tools.Invoke(\"todo\", new { timeoutSeconds = 120, action = \"Add\", description = \"ship it\" })")));
        host.Mock.Returns(E2E.ExecToolCall("call_2", E2E.ExecProgram("Tools.Invoke(\"state.set\", new { timeoutSeconds = 60, key = \"todo/list\", value = \"hijack\" })")));
        host.Mock.Returns(E2E.ExecToolCall("call_3", E2E.ExecProgram("Tools.Invoke(\"state.delete\", new { timeoutSeconds = 60, key = \"todo/list\" })")));
        host.Mock.Returns(E2E.ExecToolCall("call_4", E2E.ExecProgram("Tools.Invoke(\"todo\", new { timeoutSeconds = 120, action = \"List\" })")));
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

    /// <summary>Nested-spawn E2E, async contract: the parent session spawns a child through
    ///     agent.spawn (returns immediately with status=running and no report), then fetches
    ///     the finished child's report through agent.result. The mock plays both sides via
    ///     model-keyed scripting — the parent under its session model, the child under the
    ///     per-spawn model — and substitutes {{child_id}} with the runtime child id observed
    ///     in the parent's tool messages.</summary>
    [Fact]
    public async Task NestedSpawn_ChildRunsAndReports()
    {
        using var host = await new E2E.Host().StartAsync();

        // Parent script, keyed by the session model: spawn, status, poll-then-fetch result,
        // final text. Turn 3 polls status inside exec so the async child's terminal write is
        // observed before agent.result runs.
        const string pollThenResult = """
            var status = "";
            while (!status.Contains("status=completed"))
            {
                await System.Threading.Tasks.Task.Delay(50);
                status = Tools.Invoke("agent.status", new { timeoutSeconds = 60, id = "{{child_id}}" });
            }
            return Tools.Invoke("agent.result", new { timeoutSeconds = 60, id = "{{child_id}}" });
            """;
        host.Mock.ReturnsForModel(E2E.SessionModel,
            E2E.ExecToolCall("parent_call_1", E2E.ExecProgram("var spawned = Tools.Invoke(\"agent.spawn\", new { timeoutSeconds = 60, taskPrompt = \"Say child report done and nothing else.\", model = \"mock/sub-model\", label = \"e2e\" }); return spawned;")),
            E2E.ExecToolCall("parent_call_2", E2E.ExecProgram("return Tools.Invoke(\"agent.status\", new { timeoutSeconds = 60, id = \"{{child_id}}\" });")),
            E2E.ExecToolCall("parent_call_3", E2E.ExecProgram(pollThenResult)),
            RawCompletion("done: child reported"));

        // Child script, keyed by the per-spawn model: one tool turn, then the final report.
        host.Mock.ReturnsForModel("mock/sub-model",
            E2E.ExecToolCall("child_call_1", E2E.ExecProgram("return \"child report done\";")),
            RawCompletion("child report done"));

        await host.Vm.RunTurnAsync("delegate a subtask and fetch its result");

        var parentBodies = host.Mock.RequestBodies
            .Where(body => MockOpenRouterServer.TryGetRequestModel(body) == E2E.SessionModel)
            .ToList();
        Assert.True(parentBodies.Count >= 4,
            $"expected at least 4 parent requests, got {parentBodies.Count}");

        // (a) The spawn result reached the transcript as a running line — non-blocking:
        //     no report text, and none of the removed completed-gutter furniture.
        var spawnResult = E2E.GetLastToolMessage(parentBodies[1]);
        Assert.Matches("^id=[0-9a-fA-F-]{36} status=running$", spawnResult.Trim());
        Assert.DoesNotContain("child report done", spawnResult);
        Assert.DoesNotContain("--- report ---", spawnResult);

        // (b) Wire: the child ran its own loop against the mock under the per-spawn model id.
        Assert.Contains(host.Mock.RequestBodies,
            body => MockOpenRouterServer.TryGetRequestModel(body) == "mock/sub-model");

        // (c) Decoded transcript: the parent fetched the child's report through agent.result.
        Assert.Contains("child report done",
            E2E.FindToolMessageContaining(parentBodies, "child report done"),
            StringComparison.Ordinal);

        // (d) The parent's final reply acknowledges completion.
        var assistant = string.Join("", host.Vm.Transcript.Entries
            .OfType<AssistantTextEntry>().Select(a => a.Text));
        Assert.Contains("done:", assistant, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Seed-and-recall E2E: the first exchange seeds the persisted root transcript
    ///     with a distinctive phrase; scripted turns then list sessions and recall the phrase
    ///     through the memory capability actions inside exec programs. Assertions read only
    ///     decoded tool-message content — [mem] hit lines, the paging footer, session= lines.</summary>
    [Fact]
    public async Task MemoryRecall_AgainstMockServer()
    {
        using var host = await new E2E.Host().StartAsync();

        // Turn 1: plain assistant reply seeding 'xylophone harvest' into the transcript.
        host.Mock.Returns(RawCompletion("The xylophone harvest begins at dawn."));
        await host.Vm.RunTurnAsync("tell me about the xylophone harvest");

        // Turn 2: one exec tool call listing what conversations exist.
        host.Mock.Returns(E2E.ExecToolCall("call_1", E2E.ExecProgram("return Tools.Invoke(\"memory.sessions\", new { timeoutSeconds = 60, limit = 50 });")));
        // Turn 3: one exec tool call recalling the seeded phrase across all sessions.
        host.Mock.Returns(E2E.ExecToolCall("call_2", E2E.ExecProgram("return Tools.Invoke(\"memory.recall\", new { timeoutSeconds = 60, query = \"xylophone\", scope = \"global\" });")));
        // Turn 4: final text closes the exchange.
        host.Mock.Returns(RawCompletion("recalled."));
        await host.Vm.RunTurnAsync("now list sessions and recall what you said");

        Assert.True(host.Mock.RequestBodies.Count >= 4,
            $"expected at least 4 scripted requests, got {host.Mock.RequestBodies.Count}");

        // (a) Sessions listing shows the persisted root conversation at depth 0.
        var sessionsOutput = E2E.FindToolMessageContaining(host.Mock.RequestBodies, "label=root depth=0");
        Assert.Matches(@"(^|\n)session=[0-9a-fA-F-]{36} label=root depth=0 entries=\d+ ", sessionsOutput);

        // (b) Recall renders the [mem] annotation line carrying the seeded phrase.
        var recallOutput = E2E.FindToolMessageContaining(host.Mock.RequestBodies, "xylophone harvest");
        Assert.Contains("[mem] session=", recallOutput, StringComparison.Ordinal);

        // (c) The recall footer follows the paging contract.
        Assert.Matches(@"--- memory: \d+ hits, page 1/\d+ ---", recallOutput);

        var assistant = string.Join("", host.Vm.Transcript.Entries
            .OfType<AssistantTextEntry>().Select(a => a.Text));
        Assert.Contains("recalled.", assistant, StringComparison.OrdinalIgnoreCase);
    }}
