using System.Diagnostics;
using System.Text.RegularExpressions;
using eThangAgent.AgentDomain;
using eThangAgent.Storage.ACL;

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

    /// <summary>Nested-spawn E2E: parent session spawns a child through agent.spawn; the child
    ///     runs its own full loop against the mock under a per-spawn model id and reports back.
    ///     Asserts the wire (per-spawn model on child requests), the decoded tool message
    ///     (completed gutter + report), the rendered transcript, and persistence of exactly
    ///     one Completed depth-1 agent row with a non-empty transcript.
    ///     Skipped mid-plan: asserts the removed P4 synchronous spawn contract (completed
    ///     gutter + report at spawn time); rewritten for the async contract in plan Task 9.</summary>
    [Fact(Skip = "P4 sync-spawn contract removed by Tasks 4-5 (async runtime); rewritten in plan Task 9.")]
    public async Task Repl_NestedSpawn_ChildRunsAndReports()
    {
        using var mock = new MockOpenRouterServer();
        mock.Start();
        var db = Path.Combine(Path.GetTempPath(), $"ethang-nested-{Guid.NewGuid():N}.db");

        // Parent script: first call spawns a child via exec; second call acknowledges it.
        var spawnProgram = """
            agent.spawn @{ taskPrompt = 'summarize the meeting notes'; model = 'mock/sub-model'; label = 'e2e' }
            """;
        var spawnArgs = System.Text.Json.JsonSerializer.Serialize(new { program = spawnProgram });
        mock.Returns(ExecToolCall("parent_call_1", spawnArgs));
        mock.Returns("""{"choices":[{"message":{"content":"Delegated to sub-agent 'e2e'; its report arrived completed."}}]}""");

        // Child script, keyed by the per-spawn model: one tool turn, then the final report.
        var childProgram = System.Text.Json.JsonSerializer.Serialize(
            new { program = "Write-Output 'child exec ran'" });
        mock.ReturnsForModel("mock/sub-model",
            ExecToolCall("child_call_1", childProgram),
            """{"choices":[{"message":{"content":"child report done"}}]}""");

        using var process = StartCli(mock, db);
        var reader = process.StandardOutput;
        await ReadUntil(reader, "> ");

        await process.StandardInput.WriteLineAsync("delegate the summarization");
        await process.StandardInput.FlushAsync();

        var response = await ReadUntil(reader, "> ");
        Assert.Contains("Delegated to sub-agent 'e2e'", response, StringComparison.OrdinalIgnoreCase);

        // Wire: child request bodies carry the per-spawn model id at the top level.
        var childBodies = mock.RequestBodies
            .Where(body => MockOpenRouterServer.TryGetRequestModel(body) == "mock/sub-model")
            .ToList();
        Assert.Equal(2, childBodies.Count);
        Assert.Contains("\"tools\"", childBodies[0]);            // child's first turn carries the tool surface
        Assert.Contains("\"tool_calls\"", childBodies[1]);        // child's second turn echoes its tool call
        Assert.Contains("child exec ran", childBodies[1]);        // child saw its own tool result

        // Decoded parent tool message carries the completed gutter and fenced report.
        var toolContent = FindToolMessageContaining(mock.RequestBodies, "[agent] id=");
        Assert.Contains("status=completed", toolContent);
        Assert.Contains("depth=1", toolContent);
        Assert.Contains("model=mock/sub-model", toolContent);
        Assert.Contains("label=e2e", toolContent);
        Assert.Contains("--- report ---", toolContent);
        Assert.Contains("child report done", toolContent);

        await process.StandardInput.WriteLineAsync("/quit");
        await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        Assert.Equal(0, process.ExitCode);

        // Persistence: exactly one agent row (roots are not persisted) — Completed,
        // depth 1, non-empty transcript — read back through AppDatabase/SqliteAgentStore.
        var database = new AppDatabase(db);
        var store = new SqliteAgentStore(database);
        var childId = ParseCompletedChildId(toolContent);
        var child = await store.GetAsync(childId);
        Assert.True(child.IsSuccess);
        Assert.Equal(AgentStatus.Completed, child.Value!.Status);
        Assert.Equal(1, child.Value.Depth);
        Assert.Equal("mock/sub-model", child.Value.ModelUsed);
        Assert.Equal("e2e", child.Value.Label);
        Assert.Equal("child report done", child.Value.FinalReport);

        var transcript = await store.GetTranscriptAsync(childId);
        Assert.True(transcript.IsSuccess);
        Assert.NotEmpty(transcript.Value!);

        using (var connection = database.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM agents;";
            Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
        }

        try { File.Delete(db); } catch { }
    }

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

    private static AgentId ParseCompletedChildId(string toolContent)
    {
        var match = Regex.Match(toolContent, @"\[agent\] id=([0-9a-fA-F-]{36}) status=completed");
        Assert.True(match.Success, $"no completed-gutter id found in: {toolContent}");
        return new AgentId(Guid.Parse(match.Groups[1].Value));
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
