using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace eThangAgent.CLI.Tests;

public class E2ETests
{
    [Fact]
    public async Task Repl_RespondsToPrompt_AgainstMockServer()
    {
        using var mock = new MockOpenRouterServer();
        mock.Start();

        using var process = StartCli(mock);

        var reader = process.StandardOutput;
        var banner = await ReadUntil(reader, "> ");
        Assert.Contains("eThang Agent", banner);

        await process.StandardInput.WriteLineAsync("Say 'pineapple' and nothing else.");
        await process.StandardInput.FlushAsync();

        var response = await ReadUntil(reader, "> ");
        Assert.Contains("pineapple", response, StringComparison.OrdinalIgnoreCase);

        await process.StandardInput.WriteLineAsync("/exit");
        await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task Repl_HelpListsCommands_AndQuitExits()
    {
        using var mock = new MockOpenRouterServer();
        mock.Start();

        using var process = StartCli(mock);
        var reader = process.StandardOutput;

        var banner = await ReadUntil(reader, "> ");
        Assert.Contains("eThang Agent", banner);
        Assert.Contains("/help", banner);

        await process.StandardInput.WriteLineAsync("/help");
        await process.StandardInput.FlushAsync();
        var help = await ReadUntil(reader, "> ");
        Assert.Contains("/exit", help);
        Assert.Contains("/quit", help);
        Assert.Contains("/help", help);

        await process.StandardInput.WriteLineAsync("/quit");
        await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task Repl_SendsConfiguredDefaultModel_ToProvider()
    {
        using var mock = new MockOpenRouterServer();
        mock.Start();

        using var process = StartCli(mock);
        var reader = process.StandardOutput;

        await ReadUntil(reader, "> ");
        await process.StandardInput.WriteLineAsync("hi");
        await process.StandardInput.FlushAsync();
        await ReadUntil(reader, "> ");

        Assert.NotNull(mock.LastChatRequestBody);
        Assert.Contains("stealth/ox-alpha", mock.LastChatRequestBody);

        await process.StandardInput.WriteLineAsync("/quit");
        await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task Repl_InjectsSuperpowersBootstrap_OncePerSession()
    {
        using var mock = new MockOpenRouterServer();
        mock.Start();
        using var process = StartCli(mock);
        var reader = process.StandardOutput;

        await ReadUntil(reader, "> ");
        await process.StandardInput.WriteLineAsync("hi");
        await process.StandardInput.FlushAsync();
        await ReadUntil(reader, "> ");

        var body = mock.LastChatRequestBody;
        Assert.NotNull(body);
        // The wire body JSON-escapes angle brackets (\u003C/\u003E), so assertions on
        // injected content run against the decoded system message, not the raw body.
        using var doc = JsonDocument.Parse(body);
        var system = doc.RootElement.GetProperty("messages").EnumerateArray()
            .First(m => m.GetProperty("role").GetString() == "system")
            .GetProperty("content").GetString();
        Assert.NotNull(system);
        Assert.Contains("<EXTREMELY_IMPORTANT>", system);
        Assert.Contains("name: using-superpowers", system);
        Assert.Contains("ALREADY ACTIVE", system);
        Assert.Contains("skill_view", system);
        Assert.Equal(1, Regex.Count(system!, Regex.Escape("<EXTREMELY_IMPORTANT>")));

        await process.StandardInput.WriteLineAsync("/quit");
        await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task Repl_StateDisciplineLoop_Certifies()
    {
        using var mock = new MockOpenRouterServer();
        mock.Start();
        var db = Path.Combine(Path.GetTempPath(), $"ethang-state-{Guid.NewGuid():N}.db");

        var program = """
            state.set @{ key = 'current/head'; value = 'done' }
            state.transition @{ from = 'coding'; to = 'done'; summary = 'work'; evidence = @('Write-Output evidence-ok') }
            state.verify @{}
            """;
        var execArgs = System.Text.Json.JsonSerializer.Serialize(new { program });
        mock.Returns(ExecToolCall("call_1", execArgs));
        mock.Returns("""{"choices":[{"message":{"content":"certified"}}]}""");

        using var process = StartCli(mock, db);
        var reader = process.StandardOutput;
        await ReadUntil(reader, "> ");

        await process.StandardInput.WriteLineAsync("track the work");
        await process.StandardInput.FlushAsync();

        var response = await ReadUntil(reader, "> ");
        Assert.Contains("certified", response, StringComparison.OrdinalIgnoreCase);
        using (var doc = System.Text.Json.JsonDocument.Parse(mock.RequestBodies[1]))
        {
            var toolContent = doc.RootElement.GetProperty("messages")[3]
                .GetProperty("content").GetString() ?? "";
            Assert.Contains("\"Certified\":true", toolContent);
        }

        await process.StandardInput.WriteLineAsync("/quit");
        await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        Assert.Equal(0, process.ExitCode);

        try { File.Delete(db); } catch { }
    }

    [Fact]
    public async Task Repl_StateDisciplineLoop_Violated_OnFailingEvidence()
    {
        using var mock = new MockOpenRouterServer();
        mock.Start();
        var db = Path.Combine(Path.GetTempPath(), $"ethang-state-{Guid.NewGuid():N}.db");

        var program = """
            $null = state.set @{ key = 'current/head'; value = 'done' }
            $null = state.transition @{ from = 'coding'; to = 'done'; summary = 'work'; evidence = @('Write-Error boom') }
            state.verify @{}
            """;
        var execArgs = System.Text.Json.JsonSerializer.Serialize(new { program });
        mock.Returns(ExecToolCall("call_1", execArgs));
        mock.Returns("""{"choices":[{"message":{"content":"violated"}}]}""");

        using var process = StartCli(mock, db);
        var reader = process.StandardOutput;
        await ReadUntil(reader, "> ");

        await process.StandardInput.WriteLineAsync("track the work");
        await process.StandardInput.FlushAsync();

        var response = await ReadUntil(reader, "> ");
        Assert.Contains("violated", response, StringComparison.OrdinalIgnoreCase);
        using (var doc = System.Text.Json.JsonDocument.Parse(mock.RequestBodies[1]))
        {
            var toolContent = doc.RootElement.GetProperty("messages")[3]
                .GetProperty("content").GetString() ?? "";
            Assert.Contains("\"Certified\":false", toolContent);
            Assert.Contains("\"Violated\":true", toolContent);
            Assert.Contains("boom", toolContent);
        }

        await process.StandardInput.WriteLineAsync("/quit");
        await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        Assert.Equal(0, process.ExitCode);

        try { File.Delete(db); } catch { }
    }

    /// <summary>Nested-spawn E2E, async contract: the parent session spawns a child through
    ///     agent.spawn (returns immediately with status=running and no report), then fetches
    ///     the finished child's report through agent.result. The mock plays both sides via
    ///     model-keyed scripting — the parent under its session model, the child under the
    ///     per-spawn model — and substitutes {{child_id}} with the runtime child id observed
    ///     in the parent's tool messages, since no static script can predict it.</summary>
    [Fact]
    public async Task Repl_NestedSpawn_ChildRunsAndReports()
    {
        using var mock = new MockOpenRouterServer();
        mock.Start();
        var db = Path.Combine(Path.GetTempPath(), $"ethang-nested-{Guid.NewGuid():N}.db");

        // Parent script, keyed by the session model Program.cs configures: spawn, status,
        // poll-then-fetch result, final text. Turn 3 polls status inside exec so the async
        // child's terminal write is observed before agent.result runs.
        const string pollThenResult = """
            $status = agent.status @{ id = '{{child_id}}' }
            while ($status -notmatch 'status=completed') { Start-Sleep -Milliseconds 50; $status = agent.status @{ id = '{{child_id}}' } }
            agent.result @{ id = '{{child_id}}' }
            """;
        mock.ReturnsForModel("stealth/ox-alpha",
            ExecToolCall("parent_call_1", ExecProgram("agent.spawn @{ taskPrompt = 'Say child report done and nothing else.'; model = 'mock/sub-model'; label = 'e2e' }")),
            ExecToolCall("parent_call_2", ExecProgram("agent.status @{ id = '{{child_id}}' }")),
            ExecToolCall("parent_call_3", ExecProgram(pollThenResult)),
            """{"choices":[{"message":{"content":"done: child reported"}}]}""");

        // Child script, keyed by the per-spawn model: one tool turn, then the final report.
        mock.ReturnsForModel("mock/sub-model",
            ExecToolCall("child_call_1", ExecProgram("Write-Output 'child report done'")),
            """{"choices":[{"message":{"content":"child report done"}}]}""");

        using var process = StartCli(mock, db);
        var reader = process.StandardOutput;
        await ReadUntil(reader, "> ");

        await process.StandardInput.WriteLineAsync("delegate a subtask and fetch its result");
        await process.StandardInput.FlushAsync();

        var response = await ReadUntil(reader, "> ");

        var parentBodies = mock.RequestBodies
            .Where(body => MockOpenRouterServer.TryGetRequestModel(body) == "stealth/ox-alpha")
            .ToList();
        Assert.True(parentBodies.Count >= 4,
            $"expected at least 4 parent requests, got {parentBodies.Count}");

        // (a) The spawn result reached the transcript as a running line — non-blocking:
        //     no report text, and none of the removed P4 completed-gutter furniture.
        var spawnResult = GetLastToolMessage(parentBodies[1]);
        Assert.Matches("^id=[0-9a-fA-F-]{36} status=running$", spawnResult.Trim());
        Assert.DoesNotContain("child report done", spawnResult);
        Assert.DoesNotContain("--- report ---", spawnResult);

        // (b) Wire: the child ran its own loop against the mock under the per-spawn model id.
        Assert.Contains(mock.RequestBodies,
            body => MockOpenRouterServer.TryGetRequestModel(body) == "mock/sub-model");

        // (c) Decoded transcript: the parent fetched the child's report through agent.result.
        Assert.Contains("child report done",
            FindToolMessageContaining(parentBodies, "child report done"),
            StringComparison.Ordinal);

        // (d) The parent's final reply acknowledges completion.
        Assert.Contains("done:", response, StringComparison.OrdinalIgnoreCase);

        await process.StandardInput.WriteLineAsync("/quit");
        await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        Assert.Equal(0, process.ExitCode);

        try { File.Delete(db); } catch { }
    }

    /// <summary>Seed-and-recall E2E: the first exchange seeds the persisted root transcript
    ///     with a distinctive phrase; scripted turns then list sessions and recall the phrase
    ///     through the memory capability actions inside exec programs. Assertions read only
    ///     decoded tool-message content — the output contracts documented verbatim in
    ///     MemoryCapabilityProvider: [mem] hit lines, the paging footer, and session= lines.</summary>
    [Fact]
    public async Task Repl_MemoryRecall_AgainstMockServer()
    {
        using var mock = new MockOpenRouterServer();
        mock.Start();
        var db = Path.Combine(Path.GetTempPath(), $"ethang-memory-{Guid.NewGuid():N}.db");

        // Turn 1: plain assistant reply seeding 'xylophone harvest' into the transcript.
        mock.Returns("""{"choices":[{"message":{"content":"The xylophone harvest begins at dawn."}}]}""");
        // Turn 2: one exec tool call listing what conversations exist.
        mock.Returns(ExecToolCall("call_1", ExecProgram("memory.sessions @{ limit = 50 }")));
        // Turn 3: one exec tool call recalling the seeded phrase across all sessions.
        mock.Returns(ExecToolCall("call_2", ExecProgram("memory.recall @{ query = 'xylophone'; scope = 'global' }")));
        // Turn 4: final text closes the exchange.
        mock.Returns("""{"choices":[{"message":{"content":"recalled."}}]}""");

        using var process = StartCli(mock, db);
        var reader = process.StandardOutput;
        await ReadUntil(reader, "> ");

        await process.StandardInput.WriteLineAsync("tell me about the xylophone harvest");
        await process.StandardInput.FlushAsync();
        await ReadUntil(reader, "> ");

        await process.StandardInput.WriteLineAsync("now list sessions and recall what you said");
        await process.StandardInput.FlushAsync();

        var response = await ReadUntil(reader, "> ");
        Assert.True(mock.RequestBodies.Count >= 4,
            $"expected at least 4 scripted requests, got {mock.RequestBodies.Count}");

        // (a) Sessions listing shows the persisted root conversation at depth 0.
        var sessionsOutput = FindToolMessageContaining(mock.RequestBodies, "label=root depth=0");
        Assert.Matches(@"(^|\n)session=[0-9a-fA-F-]{36} label=root depth=0 entries=\d+ ", sessionsOutput);

        // (b) Recall renders the [mem] annotation line carrying the seeded phrase.
        var recallOutput = FindToolMessageContaining(mock.RequestBodies, "xylophone harvest");
        Assert.Contains("[mem] session=", recallOutput, StringComparison.Ordinal);

        // (c) The recall footer follows the paging contract.
        Assert.Matches(@"--- memory: \d+ hits, page 1/\d+ ---", recallOutput);

        Assert.Contains("recalled.", response, StringComparison.OrdinalIgnoreCase);

        await process.StandardInput.WriteLineAsync("/quit");
        await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        Assert.Equal(0, process.ExitCode);

        try { File.Delete(db); } catch { }
    }

    /// <summary>Serializes an exec tool-call argument carrying one PowerShell program.</summary>
    private static string ExecProgram(string program) =>
        System.Text.Json.JsonSerializer.Serialize(new { program });

    private static string ExecToolCall(string id, string arguments) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content = (string?)null,
                        tool_calls = new[]
                        {
                            new { id, type = "function", function = new { name = "exec", arguments } }
                        }
                    }
                }
            }
        });

    [Fact]
    public async Task Repl_ModelToolsContainOnlyExec()
    {
        using var mock = new MockOpenRouterServer();
        mock.Start();

        using var process = StartCli(mock);
        var reader = process.StandardOutput;
        await ReadUntil(reader, "> ");

        await process.StandardInput.WriteLineAsync("hi");
        await process.StandardInput.FlushAsync();
        await ReadUntil(reader, "> ");

        Assert.NotNull(mock.LastChatRequestBody);
        Assert.Contains("\"name\":\"exec\"", mock.LastChatRequestBody);
        Assert.DoesNotContain("\"name\":\"read\"", mock.LastChatRequestBody);

        await process.StandardInput.WriteLineAsync("/quit");
        await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task Repl_ExecutesExecTool_EndToEnd()
    {
        using var mock = new MockOpenRouterServer();
        mock.Start();

        var tempFile = Path.Combine(Path.GetTempPath(), $"ethang-exec-{Guid.NewGuid():N}.txt");
        await File.WriteAllLinesAsync(tempFile, ["alpha line", "beta line"]);

        var program = $"read @{{ path = '{tempFile}'; startLine = 1; endLine = 2 }}";
        var execArgs = System.Text.Json.JsonSerializer.Serialize(new { program });
        mock.Returns(ExecToolCall("call_1", execArgs));
        mock.Returns("""{"choices":[{"message":{"content":"exec completed"}}]}""");

        using var process = StartCli(mock);
        var reader = process.StandardOutput;
        await ReadUntil(reader, "> ");

        await process.StandardInput.WriteLineAsync("run a program");
        await process.StandardInput.FlushAsync();

        var response = await ReadUntil(reader, "> ");
        Assert.Contains("exec completed", response, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, mock.RequestBodies.Count);
        Assert.Contains("\"role\":\"tool\"", mock.RequestBodies[1]);
        Assert.Contains("alpha line", mock.RequestBodies[1]);

        await process.StandardInput.WriteLineAsync("/quit");
        await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        Assert.Equal(0, process.ExitCode);

        try { File.Delete(tempFile); } catch { }
    }

    [Fact]
    public async Task Repl_Exec_ParseErrorFeedsBack_AndCorrectedProgramSucceeds()
    {
        using var mock = new MockOpenRouterServer();
        mock.Start();

        var broken = System.Text.Json.JsonSerializer.Serialize(new { program = "if (x {" });
        var corrected = System.Text.Json.JsonSerializer.Serialize(
            new { program = "Write-Output 'corrected output'" });
        mock.Returns(ExecToolCall("call_1", broken));
        mock.Returns(ExecToolCall("call_2", corrected));
        mock.Returns("""{"choices":[{"message":{"content":"done"}}]}""");

        using var process = StartCli(mock);
        var reader = process.StandardOutput;
        await ReadUntil(reader, "> ");

        await process.StandardInput.WriteLineAsync("try exec");
        await process.StandardInput.FlushAsync();

        var response = await ReadUntil(reader, "> ");
        Assert.Contains("done", response, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(3, mock.RequestBodies.Count);
        Assert.Contains("ExecParseError", mock.RequestBodies[1]);
        Assert.Contains("ExecParseError", mock.RequestBodies[2]);
        Assert.Contains("corrected output", mock.RequestBodies[2]);

        await process.StandardInput.WriteLineAsync("/quit");
        await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task Repl_SendsExecGuide_InSystemPrompt()
    {
        using var mock = new MockOpenRouterServer();
        mock.Start();

        using var process = StartCli(mock);
        var reader = process.StandardOutput;
        await ReadUntil(reader, "> ");

        await process.StandardInput.WriteLineAsync("hi");
        await process.StandardInput.FlushAsync();
        await ReadUntil(reader, "> ");

        Assert.NotNull(mock.LastChatRequestBody);
        Assert.Contains("\"role\":\"system\"", mock.LastChatRequestBody);
        Assert.Contains("writing PowerShell programs", mock.LastChatRequestBody);
        Assert.Contains("get(key: String): Read a durable state value.", mock.LastChatRequestBody);
        Assert.Contains(
            "verify(ids: String[]): Run attached evidence fail-closed and certify.",
            mock.LastChatRequestBody);
        Assert.Contains(
            "read(path: String, startLine: Integer, endLine: Integer): Read lines from a text file.",
            mock.LastChatRequestBody);

        await process.StandardInput.WriteLineAsync("/quit");
        await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        Assert.Equal(0, process.ExitCode);
    }

    /// <summary>Returns the decoded content of the first tool message containing the marker
    ///     across all captured chat request bodies (never raw-substring on escaped bodies).</summary>
    private static string FindToolMessageContaining(IReadOnlyList<string> bodies, string marker)
    {
        foreach (var body in bodies)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("messages", out var messages))
                continue;
            foreach (var message in messages.EnumerateArray())
            {
                if (message.TryGetProperty("role", out var role)
                    && role.GetString() == "tool"
                    && message.TryGetProperty("content", out var content)
                    && content.GetString() is { } text
                    && text.Contains(marker, StringComparison.Ordinal))
                    return text;
            }
        }
        Assert.Fail($"no decoded tool message containing '{marker}' found in {bodies.Count} request bodies");
        return "";
    }

    /// <summary>Returns the decoded content of the LAST tool-role message in a chat request
    ///     body (never raw-substring on escaped bodies).</summary>
    private static string GetLastToolMessage(string body)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        string? last = null;
        foreach (var message in doc.RootElement.GetProperty("messages").EnumerateArray())
        {
            if (message.TryGetProperty("role", out var role)
                && role.GetString() == "tool"
                && message.TryGetProperty("content", out var content))
                last = content.GetString();
        }
        Assert.NotNull(last);
        return last!;
    }

    private static Process StartCli(MockOpenRouterServer mock, string? databasePath = null)
    {
        var projectDir = Path.GetFullPath(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "src", "eThangAgent.CLI"));

        var exePath = Path.Combine(projectDir, "bin", "Debug", "net10.0", "eThangAgent.CLI.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = "",
            WorkingDirectory = projectDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.EnvironmentVariables["OPENROUTER_API_KEY"] = "test-key";
        startInfo.EnvironmentVariables["OPENROUTER_BASE_URL"] = mock.BaseUrl;
        startInfo.EnvironmentVariables["SubAgent__MaxConcurrentAgents"] = "2";
        if (databasePath is not null)
            startInfo.EnvironmentVariables["ETHANG_AGENT_DB"] = databasePath;

        var process = new Process { StartInfo = startInfo };
        process.Start();
        return process;
    }

    private static async Task<string> ReadUntil(StreamReader reader, string delimiter)
    {
        var output = new List<char>();
        var buffer = new char[1];
        while (true)
        {
            var read = await reader.ReadAsync(buffer, 0, 1);
            if (read == 0) break;
            output.Add(buffer[0]);
            var tail = new string(output.ToArray()[
                Math.Max(0, output.Count - delimiter.Length)..]);
            if (tail == delimiter) break;
        }
        return new string(output.ToArray());
    }
}
