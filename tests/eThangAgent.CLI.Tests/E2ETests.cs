using System.Diagnostics;

namespace eThangAgent.CLI.Tests;

public class E2ETests
{
    [Fact]
    public async Task Repl_RespondsToPrompt_AgainstMockServer()
    {
        using var mock = new MockOpenRouterServer();
        mock.Start();

        var projectDir = Path.GetFullPath(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "src", "eThangAgent.CLI"));

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "run --no-build",
            WorkingDirectory = projectDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.EnvironmentVariables["OPENROUTER_API_KEY"] = "test-key";
        startInfo.EnvironmentVariables["OPENROUTER_BASE_URL"] = mock.BaseUrl;

        using var process = new Process { StartInfo = startInfo };
        process.Start();

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
    public async Task Repl_ExecutesReadTool_EndToEnd()
    {
        using var mock = new MockOpenRouterServer();
        mock.Start();

        var tempFile = Path.Combine(Path.GetTempPath(), $"ethang-e2e-{Guid.NewGuid():N}.txt");
        await File.WriteAllLinesAsync(tempFile, ["alpha line", "beta line", "gamma line"]);

        var toolArgs = System.Text.Json.JsonSerializer.Serialize(
            new { path = tempFile, startLine = 2, endLine = 3 });
        var toolCallResponse = System.Text.Json.JsonSerializer.Serialize(new
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
                            new
                            {
                                id = "call_1",
                                type = "function",
                                function = new { name = "read", arguments = toolArgs }
                            }
                        }
                    }
                }
            }
        });
        mock.Returns(toolCallResponse);
        mock.Returns("""{"choices":[{"message":{"content":"read completed"}}]}""");

        using var process = StartCli(mock);
        var reader = process.StandardOutput;
        await ReadUntil(reader, "> ");

        await process.StandardInput.WriteLineAsync("read that file");
        await process.StandardInput.FlushAsync();

        var response = await ReadUntil(reader, "> ");
        Assert.Contains("read completed", response, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, mock.RequestBodies.Count);
        Assert.Contains("\"role\":\"tool\"", mock.RequestBodies[1]);
        Assert.Contains("beta line", mock.RequestBodies[1]);
        Assert.Contains("gamma line", mock.RequestBodies[1]);

        await process.StandardInput.WriteLineAsync("/quit");
        await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        Assert.Equal(0, process.ExitCode);

        try { File.Delete(tempFile); } catch { }
    }

    private static Process StartCli(MockOpenRouterServer mock)
    {
        var projectDir = Path.GetFullPath(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "src", "eThangAgent.CLI"));

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "run --no-build",
            WorkingDirectory = projectDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.EnvironmentVariables["OPENROUTER_API_KEY"] = "test-key";
        startInfo.EnvironmentVariables["OPENROUTER_BASE_URL"] = mock.BaseUrl;

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
