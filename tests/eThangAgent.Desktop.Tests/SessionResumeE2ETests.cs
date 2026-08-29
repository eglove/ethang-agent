using System.Text.Json;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.Tests;

/// <summary>Headless end-to-end session resume: turn one through the real composition,
///     tab closed, session resumed through the REAL factory over the SAME database and
///     mock server — the persisted transcript replays into the tab and the next turn
///     carries the prior history to the provider.</summary>
[Collection("Desktop E2E")]
public class SessionResumeE2ETests
{
  private static string RawCompletion(string content) =>
      JsonSerializer.Serialize(
          new { choices = new[] { new { message = new { content } } } });

  private sealed class ResumeStubChannel : IClarifyChannel
  {
    public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<string>(
            new DomainError("Cancelled", "no clarify expected in this E2E scenario")));
  }

  [Fact]
  public async Task Resume_Replays_Transcript_And_Carries_History_Into_Next_Turn()
  {
    using E2E.HostHarness host = new();
    _ = await host.StartAsync();

    // Turn one lands in the persisted transcript through the normal lifecycle.
    await host.Vm.RunTurnAsync("remember the word crumble");
    AgentId rootId = host.RootId;

    // Close the tab: the lifecycle marks the row Completed and the container is
    // disposed — nothing in memory survives.
    await host.Shell.CloseTabAsync(host.Shell.Tabs[0]);
    Assert.Empty(host.Shell.Tabs);

    // Resume through the real factory over the SAME temp database and mock server.
    AgentSessionFactory factory = host.CreateResumeFactory();
    Result<AgentSession> resumed = await factory.ResumeAsync(rootId, new ResumeStubChannel());
    Assert.True(resumed.IsSuccess);
    AgentSession session = resumed.Value;

    // The conversation hydrates losslessly from the persisted transcript.
    Assert.Equal(rootId, session.RootId);
    Assert.Equal("remember the word crumble", session.Conversation.Messages[0].Content);
    Assert.Equal(2, session.Conversation.Messages.Count);

    // Pin the model the way the desktop's per-workspace preference does, so no
    // selection run consumes the mock's scripted responses.
    session.Preferences!.ModelId = E2E.SessionModel;

    // Open through the shell surface: the transcript replays into the view-model.
    AgentSessionViewModel? sessionVmRef = null;
    MainViewModel shell = await MainViewModel.ForPrebuiltSessionAsync(session, evt =>
        (sessionVmRef ?? throw new InvalidOperationException("sink fired before initialization"))
            .ApplyUiStreamEventAsync(evt));
    AgentSessionViewModel vm = shell.Tabs[0].ViewModel;
    sessionVmRef = vm;

    Assert.Equal(2, vm.Transcript.Entries.Count);
    Assert.Equal("remember the word crumble",
        Assert.IsType<UserMessageEntry>(vm.Transcript.Entries[0]).Text);
    Assert.Contains("pineapple",
        Assert.IsType<AssistantTextEntry>(vm.Transcript.Entries[1]).Text, StringComparison.Ordinal);

    // Turn two: the resumed session's request must CARRY the prior history.
    _ = host.Mock.Returns(RawCompletion("crumble, of course"));
    await vm.RunTurnAsync("what was the word?");

    Assert.Equal(2, host.Mock.RequestBodies.Count);
    string secondTurn = host.Mock.RequestBodies[1];
    Assert.Contains("remember the word crumble", secondTurn, StringComparison.Ordinal);
    Assert.Contains("pineapple", secondTurn, StringComparison.Ordinal);
    Assert.Contains("what was the word?", secondTurn, StringComparison.Ordinal);

    string assistant = string.Join("", vm.Transcript.Entries
        .OfType<AssistantTextEntry>().Select(a => a.Text));
    Assert.Contains("crumble, of course", assistant, StringComparison.Ordinal);
  }
}
