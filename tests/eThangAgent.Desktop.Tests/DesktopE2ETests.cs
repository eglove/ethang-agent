using System.Text.Json;
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
      JsonSerializer.Serialize(
          new { choices = new[] { new { message = new { content } } } });

  [Fact]
  public async Task Turn_SendsConfiguredDefaultModel_ToProvider()
  {
    using E2E.HostHarness host = new();
    _ = await host.StartAsync();

    await host.Vm.RunTurnAsync("hi");

    Assert.NotNull(host.Mock.LastChatRequestBody);
    Assert.Contains(E2E.SessionModel, host.Mock.LastChatRequestBody, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Turn_InjectsSkillsBootstrap_OncePerSession()
  {
    using E2E.HostHarness host = new();
    _ = await host.StartAsync();

    await host.Vm.RunTurnAsync("hi");

    string? body = host.Mock.LastChatRequestBody;
    Assert.NotNull(body);
    // The wire body JSON-escapes angle brackets (\u003C/\u003E), so assertions on
    // injected content run against the decoded system message, not the raw body.
    using JsonDocument doc = JsonDocument.Parse(body);
    string? system = doc.RootElement.GetProperty("messages").EnumerateArray()
        .First(m => m.GetProperty("role").GetString() == "system")
        .GetProperty("content").GetString();
    Assert.NotNull(system);
    Assert.Contains("<EXTREMELY_IMPORTANT>", system, StringComparison.Ordinal);
    Assert.Contains("name: using-skills", system, StringComparison.Ordinal);
    Assert.Contains("ALREADY ACTIVE", system, StringComparison.Ordinal);
    Assert.Contains("skill_view", system, StringComparison.Ordinal);
    Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(system,
        System.Text.RegularExpressions.Regex.Escape("<EXTREMELY_IMPORTANT>")));
  }

  [Fact]
  public async Task ModelToolsContainOnlyExec()
  {
    using E2E.HostHarness host = new();
    _ = await host.StartAsync();

    await host.Vm.RunTurnAsync("hi");

    Assert.NotNull(host.Mock.LastChatRequestBody);
    Assert.Contains("\"name\":\"exec\"", host.Mock.LastChatRequestBody, StringComparison.Ordinal);
    Assert.DoesNotContain("\"name\":\"read\"", host.Mock.LastChatRequestBody, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SendsExecGuide_InSystemPrompt()
  {
    using E2E.HostHarness host = new();
    _ = await host.StartAsync();

    await host.Vm.RunTurnAsync("hi");

    Assert.NotNull(host.Mock.LastChatRequestBody);
    Assert.Contains("\"role\":\"system\"", host.Mock.LastChatRequestBody, StringComparison.Ordinal);
    Assert.Contains("writing C# programs", host.Mock.LastChatRequestBody, StringComparison.Ordinal);
    // Stable fragments only: parameter lists change with legitimate descriptor
    // evolution; action names and summaries are the durable contract.
    Assert.Contains("get(", host.Mock.LastChatRequestBody, StringComparison.Ordinal);
    Assert.Contains("Read a durable state value", host.Mock.LastChatRequestBody, StringComparison.Ordinal);
    Assert.Contains(
        "verify(ids: String[]): Run attached evidence fail-closed and certify.",
        host.Mock.LastChatRequestBody, StringComparison.Ordinal);
    Assert.Contains(
        "read(timeoutSeconds: WholeNumber, path: Text, startLine: WholeNumber, endLine: WholeNumber): Read lines from a text file.",
        host.Mock.LastChatRequestBody, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ExecutesExecTool_EndToEnd()
  {
    using E2E.HostHarness host = new();
    _ = await host.StartAsync();

    string tempFile = Path.Combine(Path.GetTempPath(), $"ethang-exec-{Guid.NewGuid():N}.txt");
    await File.WriteAllLinesAsync(tempFile, ["alpha line", "beta line"]);

    string pathArg = tempFile.Replace("\\", "\\\\", StringComparison.Ordinal);
    string program = $"return Tools.read(new {{ timeoutSeconds = 120, path = \"{pathArg}\", startLine = 1, endLine = 2 }});";
    _ = host.Mock.Returns(E2E.ExecToolCall("call_1", E2E.ExecProgram(program)));
    _ = host.Mock.Returns(RawCompletion("exec completed"));

    await host.Vm.RunTurnAsync("run a program");

    string assistant = string.Join("", host.Vm.Transcript.Entries
        .OfType<AssistantTextEntry>().Select(a => a.Text));
    Assert.Contains("exec completed", assistant, StringComparison.OrdinalIgnoreCase);
    Assert.Equal(2, host.Mock.RequestBodies.Count);
    Assert.Contains("\"role\":\"tool\"", host.Mock.RequestBodies[1], StringComparison.Ordinal);
    Assert.Contains("alpha line", host.Mock.RequestBodies[1], StringComparison.Ordinal);

    // Named decision (CA1031): temp-file cleanup is best effort.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      File.Delete(tempFile);
    }
    catch { }
#pragma warning restore CA1031
  }

  [Fact]
  public async Task Exec_ParseErrorFeedsBack_AndCorrectedProgramSucceeds()
  {
    using E2E.HostHarness host = new();
    _ = await host.StartAsync();

    string broken = JsonSerializer.Serialize(new { program = "if (x {" });
    string corrected = JsonSerializer.Serialize(
        new { program = "Write-Output 'corrected output'" });
    _ = host.Mock.Returns(E2E.ExecToolCall("call_1", broken));
    _ = host.Mock.Returns(E2E.ExecToolCall("call_2", corrected));
    _ = host.Mock.Returns(RawCompletion("done"));

    await host.Vm.RunTurnAsync("try exec");

    string assistant = string.Join("", host.Vm.Transcript.Entries
        .OfType<AssistantTextEntry>().Select(a => a.Text));
    Assert.Contains("done", assistant, StringComparison.OrdinalIgnoreCase);

    Assert.Equal(3, host.Mock.RequestBodies.Count);
    Assert.Contains("ExecParseError", host.Mock.RequestBodies[1], StringComparison.Ordinal);
    Assert.Contains("ExecParseError", host.Mock.RequestBodies[2], StringComparison.Ordinal);
    Assert.Contains("corrected output", host.Mock.RequestBodies[2], StringComparison.Ordinal);
  }
}
