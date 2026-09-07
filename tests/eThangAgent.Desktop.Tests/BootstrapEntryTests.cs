using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>
/// The session bootstrap entry: every open (fresh or resumed) appends one
/// context entry as the transcript's first line - workspace, provider, model,
/// session id - so the chat surface shows what the session runs under before
/// any content. The entry also carries the session's rendered system prompt
/// (collapsed by default in the UI - nothing the agent receives is hidden).
/// Names the production change that would fail each test: removing the
/// constructor append empties the entry list; dropping the prompt
/// pass-through loses the verbatim text.
/// </summary>
public class BootstrapEntryTests
{
  [Fact]
  public void Construction_Appends_Bootstrap_Entry_As_First_Transcript_Entry()
  {
    AgentSessionViewModel vm = Build();

    TranscriptEntry entry = Assert.Single(vm.Transcript.Entries);
    _ = Assert.IsType<BootstrapEntry>(entry);
  }

  [Fact]
  public void Bootstrap_Entry_Carries_Session_Context()
  {
    AgentSessionViewModel vm = Build();

    BootstrapEntry entry = Assert.IsType<BootstrapEntry>(vm.Transcript.Entries[0]);
    Assert.Equal(@"C:\work\demo", entry.WorkspaceRoot);
    Assert.Equal("OpenRouter", entry.Provider);
    Assert.Equal("test/model", entry.ModelId);
    Assert.Equal(vm.SessionIdShort, entry.SessionId8);
  }

  [Fact]
  public void Bootstrap_Entry_Carries_The_Rendered_System_Prompt_Verbatim()
  {
    AgentSessionViewModel vm = Build(systemPrompt: "YOU ARE ETHANG AGENT - verbatim system prompt");

    BootstrapEntry entry = Assert.IsType<BootstrapEntry>(vm.Transcript.Entries[0]);
    Assert.Equal("YOU ARE ETHANG AGENT - verbatim system prompt", entry.SystemPrompt);
  }

  [Fact]
  public void Bootstrap_Entry_Without_Wired_Prompt_Carries_Empty_Text()
  {
    AgentSessionViewModel vm = Build();

    BootstrapEntry entry = Assert.IsType<BootstrapEntry>(vm.Transcript.Entries[0]);
    Assert.Equal(string.Empty, entry.SystemPrompt);
  }

  [Fact]
  public void Resume_Restore_Lands_After_Bootstrap_Entry()
  {
    AgentSessionViewModel vm = Build();
    DateTimeOffset ts = DateTimeOffset.Now;
    vm.Transcript.Restore(
    [
      new Message(Role.User, "hi", ts),
      new Message(Role.Assistant, "hello", ts),
    ]);

    _ = Assert.IsType<BootstrapEntry>(vm.Transcript.Entries[0]);
    _ = Assert.IsType<UserMessageEntry>(vm.Transcript.Entries[1]);
    _ = Assert.IsType<AssistantTextEntry>(vm.Transcript.Entries[2]);
  }

  private static AgentSessionViewModel Build(string? systemPrompt = null)
  {
    return new AgentSessionViewModel(
        NoopRunner,
        new RootSessionLifecycle(new TestFixtures.StubStore()),
        AgentId.NewId(),
        new Conversation(),
        provider: "OpenRouter",
        modelId: "test/model",
        new AgentSessionViewModelOptions { WorkspaceRoot = @"C:\work\demo", SystemPrompt = systemPrompt ?? string.Empty });

#pragma warning disable IDE0060, S1172 // Delegate-shape parameters are unused by design.
    static Task<Result<string>> NoopRunner(
        SendMessageCommand _command, CancellationToken _ct, TurnCallbacks? _callbacks, Action<string>? _onNotice)
#pragma warning restore IDE0060, S1172
      => Task.FromResult(Result.Success("unused"));
  }
}
