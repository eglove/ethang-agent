using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

/// <summary>
/// Headless end-to-end coverage ported from the retired piped-CLI suite: the REAL
/// composition answers through the mock provider and the view-model renders it.
/// Provider-contract scenarios: configured model selection, superpowers bootstrap,
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
        using var host = new E2E.Host().Start();

        await host.Vm.RunTurnAsync("hi");

        Assert.NotNull(host.Mock.LastChatRequestBody);
        Assert.Contains(E2E.SessionModel, host.Mock.LastChatRequestBody);
    }

    [Fact]
    public async Task Turn_InjectsSuperpowersBootstrap_OncePerSession()
    {
        using var host = new E2E.Host().Start();

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
        Assert.Contains("name: using-superpowers", system);
        Assert.Contains("ALREADY ACTIVE", system);
        Assert.Contains("skill_view", system);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(system!,
            System.Text.RegularExpressions.Regex.Escape("<EXTREMELY_IMPORTANT>")));
    }

    [Fact]
    public async Task ModelToolsContainOnlyExec()
    {
        using var host = new E2E.Host().Start();

        await host.Vm.RunTurnAsync("hi");

        Assert.NotNull(host.Mock.LastChatRequestBody);
        Assert.Contains("\"name\":\"exec\"", host.Mock.LastChatRequestBody);
        Assert.DoesNotContain("\"name\":\"read\"", host.Mock.LastChatRequestBody);
    }

    [Fact]
    public async Task SendsExecGuide_InSystemPrompt()
    {
        using var host = new E2E.Host().Start();

        await host.Vm.RunTurnAsync("hi");

        Assert.NotNull(host.Mock.LastChatRequestBody);
        Assert.Contains("\"role\":\"system\"", host.Mock.LastChatRequestBody);
        Assert.Contains("writing C# programs", host.Mock.LastChatRequestBody);
        Assert.Contains("get(key: String): Read a durable state value.", host.Mock.LastChatRequestBody);
        Assert.Contains(
            "verify(ids: String[]): Run attached evidence fail-closed and certify.",
            host.Mock.LastChatRequestBody);
        Assert.Contains(
            "read(path: String, startLine: Integer, endLine: Integer): Read lines from a text file.",
            host.Mock.LastChatRequestBody);
    }
}