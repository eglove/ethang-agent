using System.Text.Json;
using eThangAgent.Composition;
using eThangAgent.Desktop.Streaming;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;

using eThangAgent.TestSupport;

namespace eThangAgent.Desktop.Tests;

/// <summary>Invocation channel end-to-end (plan #30 task 6): over the real
///     composition and the mock provider, a manual file skill in a configured
///     directory flows through the skill_invoke tool — the System message lands in the
///     conversation ahead of the assistant's visible reply and the tool result is the
///     one-line confirmation. The Desktop composer reaches the same skill through its
///     <c>@</c> reference (the provider request carries the invocation line + full body
///     BEFORE the user message), and a slash name outside the closed command registry
///     sends no injected message. The popup offers the same two surfaces: the catalog's
///     skills at <c>@</c> and the registry's commands at <c>/</c>.</summary>
[Collection("Desktop E2E")]
[Trait("Category", "Process")]
public class SkillSlashE2ETests
{
  private const string SkillName = "e2e-invoke-skill";
  private const string SkillBody = "Body of the e2e invocation skill: verify the deploy checklist.";

  private static string RawCompletion(string content) => E2E.RawCompletion(content);

  /// <summary>Decodes one responses request body into its (role, content) list: message
  ///     input items map to their role and joined content-part text; tool results
  ///     (function_call_output items) map to the "tool" role.</summary>
  private static List<(string Role, string Content)> MessagesOf(string body)
  {
    List<(string, string)> messages = [];
    using JsonDocument doc = JsonDocument.Parse(body);
    foreach (JsonElement item in doc.RootElement.GetProperty("input").EnumerateArray())
    {
      string type = item.TryGetProperty("type", out JsonElement itemType) ? itemType.GetString() ?? "" : "";
      if (type == "message")
      {
        messages.Add((
            item.GetProperty("role").GetString() ?? "",
            JoinedContentText(item.GetProperty("content"))));
      }
      else if (type == "function_call_output")
      {
        JsonElement output = item.GetProperty("output");
        messages.Add(("tool", output.ValueKind == JsonValueKind.String
            ? output.GetString() ?? ""
            : JoinedContentText(output)));
      }
    }

    return messages;
  }

  /// <summary>Joins a content-part array's text parts (input_text on the request wire).</summary>
  private static string JoinedContentText(JsonElement content)
  {
    if (content.ValueKind == JsonValueKind.String)
    {
      return content.GetString() ?? "";
    }

    System.Text.StringBuilder joined = new();
    foreach (JsonElement part in content.EnumerateArray())
    {
      if (part.TryGetProperty("text", out JsonElement text)
          && text.ValueKind == JsonValueKind.String)
      {
        _ = joined.Append(text.GetString());
      }
    }

    return joined.ToString();
  }

  private static bool IsInvocationSystem((string Role, string Content) m) =>
      m.Role == "system" && m.Content.StartsWith("[skill invoked: " + SkillName + "]", StringComparison.Ordinal);

  [Fact]
  public async Task Tool_Path_And_Slash_Path_Carry_Invocation()
  {
    DirectoryInfo ws = TestTempRoot.CreateDirectory("ethang-e2e-skillslash-ws");
    DirectoryInfo skills = TestTempRoot.CreateDirectory("ethang-e2e-skillslash-dir");
    // 'await using' rather than a finally: the harness's own teardown stops the shell's
    // sessions and releases its storage, which a bare Dispose cannot do.
#pragma warning disable CA2007 // 'await using' cannot carry ConfigureAwait.
    await using E2E.HostHarness host = new();
#pragma warning restore CA2007
    AgentSession? session = null;
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
      session = opened.Value;
      session.Preferences!.ModelId = E2E.SessionModel;

      AgentSessionViewModel? vmRef = null;
      async Task Sink(UiStreamEvent evt) =>
          await (vmRef ?? throw new InvalidOperationException("sink before view-model init"))
              .ApplyUiStreamEventAsync(evt).ConfigureAwait(true);
      MainViewModel shell = await MainViewModel.ForPrebuiltSessionAsync(session, Sink);
      AgentSessionViewModel vm = shell.Tabs[0].ViewModel;
      vmRef = vm;

      // ── The composer's popup, over the production wiring ─────────────────
      // '@' lists the catalog's skills and '/' lists the registry's commands.
      // One concatenated list serves both triggers (spec 9), so a query may match a skill
      // and a command at once: each surface is asserted by membership, not by count.
      ComposerReferenceViewModel popup = Assert.IsType<ComposerReferenceViewModel>(vm.ReferenceCompletions);
      vm.UpdateReferenceCompletions("@" + SkillName, SkillName.Length + 1);
      Assert.True(popup.IsOpen);
      Assert.Contains(popup.Options, option =>
          option.Name == SkillName
          && option.Kind == ReferenceKind.Skill
          && option.InsertText == "@" + SkillName);

      vm.UpdateReferenceCompletions("/ne", 3);
      Assert.True(popup.IsOpen);
      Assert.Contains(popup.Options, option =>
          option.Name == "new"
          && option.Kind == ReferenceKind.Command
          && option.InsertText == "/new");
      popup.Close();

      // ── Turn 1: the TOOL path — the model calls skill_invoke through the
      // loop's exec surface (the advertised tool), invoking the capability. ──
      string invokeProgram =
          "return Tools.Invoke(\"skill_invoke\", new { timeoutSeconds = 120, name = \"" + SkillName + "\" }); return;";
      _ = host.Mock.ReturnsForModel(E2E.SessionModel,
          E2E.ExecToolCall("invoke_1", E2E.ExecProgram(invokeProgram)),
          RawCompletion("skill loaded, proceeding"));

      await vm.RunTurnAsync("invoke my deploy skill").WaitAsync(TestCeiling.Value, TestContext.Current.CancellationToken);

      // The tool result is the one-line confirmation — never the body.
      string toolResult = E2E.WithoutExecAnnotations(
          E2E.FindToolMessageContaining(host.Mock.RequestBodies, "[skill invoked: " + SkillName + "]"));
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

      // ── Turn 2: the DESKTOP reference path at the view-model seam. ────────
      _ = host.Mock.ReturnsForModel(E2E.SessionModel, RawCompletion("reference done"));
      await vm.RunTurnAsync($"run @{SkillName}").WaitAsync(TestCeiling.Value, TestContext.Current.CancellationToken);

      last = MessagesOf(host.Mock.LastChatRequestBody!);
      int slashInvocation = last.FindLastIndex(IsInvocationSystem);
      Assert.True(slashInvocation >= 0, "no invocation message in the reference request");
      Assert.Contains(SkillBody, last[slashInvocation].Content, StringComparison.Ordinal);
      int userIndex = last.FindIndex(m => m.Role == "user" && m.Content == $"run @{SkillName}");
      Assert.True(userIndex > slashInvocation, "the invocation message must precede the user message");

      // ── Turn 3: an unknown slash name is a plain message — no injection. ──
      _ = host.Mock.ReturnsForModel(E2E.SessionModel, RawCompletion("plain"));
      await vm.RunTurnAsync("/zzz-not-a-skill").WaitAsync(TestCeiling.Value, TestContext.Current.CancellationToken);

      last = MessagesOf(host.Mock.LastChatRequestBody!);
      Assert.Equal(("user", "/zzz-not-a-skill"), last[^1]);
      Assert.Equal(1, last.Count(IsInvocationSystem)); // the tool path's one; no new injection
    }
    finally
    {
      // DISPOSE THE SESSION FIRST, exactly like RemoteHostE2ETests. This test opens a
      // session whose conversation host is a LIVE PROCESS; until that container is disposed
      // the host keeps the temp workspace pinned, and Directory.Delete on a directory a live
      // process holds BLOCKS rather than throwing - which is why a try/catch could never
      // help and why the run sat in teardown past 700s with every assertion already made.
      if (session is not null)
      {
        await session.Services.DisposeAsync();
      }

      // Best effort, the sibling's exact shape: a still-draining host may pin the directory.
#pragma warning disable CA1031 // Named decision: temp-dir cleanup is best effort.
      try
      {
        ws.Delete(true);
      }
      catch (IOException)
      {
        // best effort: a still-draining host may pin the directory
      }

      try
      {
        skills.Delete(true);
      }
      catch (IOException)
      {
        // best effort: a still-draining host may pin the directory
      }
#pragma warning restore CA1031
    }
  }
}
