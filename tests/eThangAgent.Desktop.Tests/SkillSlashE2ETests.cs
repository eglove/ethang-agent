using System.Text.Json;
using eThangAgent.Composition;
using eThangAgent.Desktop.Streaming;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>Invocation channel end-to-end (plan #30 task 6): over the real
///     composition and the mock provider, a manual file skill in a configured
///     directory flows through BOTH channels — the skill_invoke tool (the System
///     message lands in the conversation ahead of the assistant's visible reply,
///     the tool result is the one-line confirmation) and the Desktop slash path
///     simulated at the view-model seam (the provider request carries the
///     invocation line + full body BEFORE the user message, with the arguments
///     line). An unknown slash name sends no injected message.</summary>
[Collection("Desktop E2E")]
public class SkillSlashE2ETests
{
  private const string SkillName = "e2e-invoke-skill";
  private const string SkillBody = "Body of the e2e invocation skill: verify the deploy checklist.";

  private static string RawCompletion(string content) => JsonSerializer.Serialize(new
  {
    choices = new[]
      {
        new { message = new { content } },
      },
  });

  /// <summary>Decodes one chat request body into its (role, content) list.</summary>
  private static List<(string Role, string Content)> MessagesOf(string body)
  {
    List<(string, string)> messages = [];
    using JsonDocument doc = JsonDocument.Parse(body);
    foreach (JsonElement message in doc.RootElement.GetProperty("messages").EnumerateArray())
    {
      messages.Add((message.GetProperty("role").GetString() ?? "", message.GetProperty("content").GetString() ?? ""));
    }

    return messages;
  }

  private static bool IsInvocationSystem((string Role, string Content) m) =>
      m.Role == "system" && m.Content.StartsWith("[skill invoked: " + SkillName + "]", StringComparison.Ordinal);

  [Fact]
  public async Task Tool_Path_And_Slash_Path_Carry_Invocation()
  {
    DirectoryInfo ws = Directory.CreateTempSubdirectory("ethang-e2e-skillslash-ws");
    DirectoryInfo skills = Directory.CreateTempSubdirectory("ethang-e2e-skillslash-dir");
    try
    {
      // One manual file skill: the channel resolves it, the listing never shows it.
      string skillFolder = Path.Combine(skills.FullName, SkillName);
      _ = Directory.CreateDirectory(skillFolder);
      await File.WriteAllLinesAsync(Path.Combine(skillFolder, "SKILL.md"),
      [
        "---",
        $"name: {SkillName}",
        "description: Manual file skill proving the invocation channel end to end.",
        "disable-model-invocation: true",
        "---",
        "",
        SkillBody,
        "",
      ], TestContext.Current.CancellationToken);

      using E2E.HostHarness host = new();
      _ = await host.StartAsync(workspaceRoot: ws.FullName).ConfigureAwait(true);
      _ = await host.Store.SetAsync(
          SkillDirectoryPreferences.WorkspaceKey(ws.FullName),
          SkillDirectoryPreferences.Serialize([new SessionFileEntry(skills.FullName, Enabled: true)]),
          TestContext.Current.CancellationToken);

      // Production open path: the factory build wires the invocation core; the
      // shell attach surfaces it on the session view-model.
      AgentSessionFactory factory = host.CreateResumeFactory();
      Result<AgentSession> opened = await factory.CreateAsync(
          ws.FullName, Providers.OpenRouter, ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(opened.IsSuccess, opened.Error?.Message);
      AgentSession session = opened.Value;
      session.Preferences!.ModelId = E2E.SessionModel;

      AgentSessionViewModel? vmRef = null;
      async Task Sink(UiStreamEvent evt) =>
          await (vmRef ?? throw new InvalidOperationException("sink before view-model init"))
              .ApplyUiStreamEventAsync(evt).ConfigureAwait(true);
      MainViewModel shell = await MainViewModel.ForPrebuiltSessionAsync(session, Sink);
      AgentSessionViewModel vm = shell.Tabs[0].ViewModel;
      vmRef = vm;
      Assert.NotNull(vm.Autocomplete);

      // ── Turn 1: the TOOL path — the model calls skill_invoke through the
      // loop's exec surface (the advertised tool), invoking the capability. ──
      string invokeProgram =
          "return Tools.Invoke(\"skill_invoke\", new { timeoutSeconds = 120, name = \"" + SkillName + "\" }); return;";
      _ = host.Mock.ReturnsForModel(E2E.SessionModel,
          E2E.ExecToolCall("invoke_1", E2E.ExecProgram(invokeProgram)),
          RawCompletion("skill loaded, proceeding"));

      await vm.RunTurnAsync("invoke my deploy skill").WaitAsync(
          TimeSpan.FromSeconds(45), TestContext.Current.CancellationToken);

      // The tool result is the one-line confirmation — never the body.
      string toolResult = E2E.FindToolMessageContaining(host.Mock.RequestBodies, "[skill invoked: " + SkillName + "]");
      Assert.Equal($"[skill invoked: {SkillName}] (no args)", toolResult);

      // The final request carries the invocation System message after the user
      // turn — ahead of the assistant's next visible reply — with the body inline.
      // (It lands at invocation time: between the assistant's tool_call and the
      // exec tool result that triggered it.)
      List<(string Role, string Content)> last = MessagesOf(host.Mock.LastChatRequestBody!);
      int invocation = last.FindIndex(IsInvocationSystem);
      Assert.True(invocation >= 0, "no invocation system message in the final request");
      Assert.Contains(SkillBody, last[invocation].Content, StringComparison.Ordinal);
      Assert.Contains(last, m => m.Role == "tool");

      // ── Turn 2: the DESKTOP slash path at the view-model seam. ────────────
      _ = host.Mock.ReturnsForModel(E2E.SessionModel, RawCompletion("slash done"));
      await vm.RunTurnAsync($"/{SkillName} run it").WaitAsync(
          TimeSpan.FromSeconds(45), TestContext.Current.CancellationToken);

      last = MessagesOf(host.Mock.LastChatRequestBody!);
      int slashInvocation = last.FindLastIndex(IsInvocationSystem);
      Assert.True(slashInvocation >= 0, "no invocation system message in the slash request");
      Assert.Contains(SkillBody, last[slashInvocation].Content, StringComparison.Ordinal);
      Assert.Contains("arguments: run it", last[slashInvocation].Content, StringComparison.Ordinal);
      int userIndex = last.FindIndex(m => m.Role == "user" && m.Content == $"/{SkillName} run it");
      Assert.True(userIndex > slashInvocation, "the invocation message must precede the user message");

      // ── Turn 3: an unknown slash name is a plain message — no injection. ──
      _ = host.Mock.ReturnsForModel(E2E.SessionModel, RawCompletion("plain"));
      await vm.RunTurnAsync("/zzz-not-a-skill").WaitAsync(
          TimeSpan.FromSeconds(45), TestContext.Current.CancellationToken);

      last = MessagesOf(host.Mock.LastChatRequestBody!);
      Assert.Equal(("user", "/zzz-not-a-skill"), last[^1]);
      Assert.Equal(2, last.Count(IsInvocationSystem)); // history's two; no new injection
    }
    finally
    {
      ws.Delete(true);
      skills.Delete(true);
    }
  }
}
