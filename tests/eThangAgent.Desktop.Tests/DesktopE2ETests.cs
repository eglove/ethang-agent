using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

/// <summary>
/// Headless end-to-end coverage ported from the retired piped-CLI suite: the REAL
/// composition answers through the mock provider and the view-model renders it.
/// Provider-contract scenarios: configured model selection, skills bootstrap,
/// exposed tool surface, exec guide injection.
/// </summary>
[Collection("Desktop E2E")]
public class DesktopE2ETests
{
    private static string RawCompletion(string content) =>
        System.Text.Json.JsonSerializer.Serialize(
            new { choices = new[] { new { message = new { content } } } });

    [Fact]
    public async Task Turn_SendsConfiguredDefaultModel_ToProvider()
    {
        using var host = await new E2E.Host().StartAsync();

        await host.Vm.RunTurnAsync("hi");

        Assert.NotNull(host.Mock.LastChatRequestBody);
        Assert.Contains(E2E.SessionModel, host.Mock.LastChatRequestBody);
    }

    [Fact]
    public async Task Turn_InjectsSkillsBootstrap_OncePerSession()
    {
        using var host = await new E2E.Host().StartAsync();

        await host.Vm.RunTurnAsync("hi");

        var body = host.Mock.LastChatRequestBody;
        Assert.NotNull(body);
        // The wire body JSON-escapes angle brackets (\u003C/\u003E), so assertions on
        // injected content run against the decoded system message, not the raw body.
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var system = doc.RootElement.GetProperty("messages").EnumerateArray()
            .First(m => m.GetProperty("role").GetString() == "system")
            .GetProperty("content").GetString();
        Assert.NotNull(system);
        Assert.Contains("<EXTREMELY_IMPORTANT>", system);
        Assert.Contains("name: using-skills", system);
        Assert.Contains("ALREADY ACTIVE", system);
        Assert.Contains("skill_view", system);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(system!,
            System.Text.RegularExpressions.Regex.Escape("<EXTREMELY_IMPORTANT>")));
    }

    [Fact]
    public async Task ModelToolsContainOnlyExec()
    {
        using var host = await new E2E.Host().StartAsync();

        await host.Vm.RunTurnAsync("hi");

        Assert.NotNull(host.Mock.LastChatRequestBody);
        Assert.Contains("\"name\":\"exec\"", host.Mock.LastChatRequestBody);
        Assert.DoesNotContain("\"name\":\"read\"", host.Mock.LastChatRequestBody);
    }

    [Fact]
    public async Task SendsExecGuide_InSystemPrompt()
    {
        using var host = await new E2E.Host().StartAsync();

        await host.Vm.RunTurnAsync("hi");

        Assert.NotNull(host.Mock.LastChatRequestBody);
        Assert.Contains("\"role\":\"system\"", host.Mock.LastChatRequestBody);
        Assert.Contains("writing C# programs", host.Mock.LastChatRequestBody);
        Assert.Contains("get(key: String, startLine: Integer, endLine: Integer): Read a durable state value, or a line range of it.", host.Mock.LastChatRequestBody);
        Assert.Contains(
            "verify(ids: String[]): Run attached evidence fail-closed and certify.",
            host.Mock.LastChatRequestBody);
        Assert.Contains(
            "read(timeoutSeconds: Integer, path: String, startLine: Integer, endLine: Integer): Read lines from a text file.",
            host.Mock.LastChatRequestBody);
    }

    [Fact]
    public async Task ExecutesExecTool_EndToEnd()
    {
        using var host = await new E2E.Host().StartAsync();

        var tempFile = Path.Combine(Path.GetTempPath(), $"ethang-exec-{Guid.NewGuid():N}.txt");
        await File.WriteAllLinesAsync(tempFile, ["alpha line", "beta line"]);

        var pathArg = tempFile.Replace("\\", "\\\\");
        var program = $"return Tools.read(new {{ timeoutSeconds = 120, path = \"{pathArg}\", startLine = 1, endLine = 2 }});";
        host.Mock.Returns(E2E.ExecToolCall("call_1", E2E.ExecProgram(program)));
        host.Mock.Returns(RawCompletion("exec completed"));

        await host.Vm.RunTurnAsync("run a program");

        var assistant = string.Join("", host.Vm.Transcript.Entries
            .OfType<AssistantTextEntry>().Select(a => a.Text));
        Assert.Contains("exec completed", assistant, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, host.Mock.RequestBodies.Count);
        Assert.Contains("\"role\":\"tool\"", host.Mock.RequestBodies[1]);
        Assert.Contains("alpha line", host.Mock.RequestBodies[1]);

        try { File.Delete(tempFile); } catch { }
    }

    [Fact]
    public async Task Exec_ParseErrorFeedsBack_AndCorrectedProgramSucceeds()
    {
        using var host = await new E2E.Host().StartAsync();

        var broken = System.Text.Json.JsonSerializer.Serialize(new { program = "if (x {" });
        var corrected = System.Text.Json.JsonSerializer.Serialize(
            new { program = "Write-Output 'corrected output'" });
        host.Mock.Returns(E2E.ExecToolCall("call_1", broken));
        host.Mock.Returns(E2E.ExecToolCall("call_2", corrected));
        host.Mock.Returns(RawCompletion("done"));

        await host.Vm.RunTurnAsync("try exec");

        var assistant = string.Join("", host.Vm.Transcript.Entries
            .OfType<AssistantTextEntry>().Select(a => a.Text));
        Assert.Contains("done", assistant, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(3, host.Mock.RequestBodies.Count);
        Assert.Contains("ExecParseError", host.Mock.RequestBodies[1]);
        Assert.Contains("ExecParseError", host.Mock.RequestBodies[2]);
        Assert.Contains("corrected output", host.Mock.RequestBodies[2]);
    }}