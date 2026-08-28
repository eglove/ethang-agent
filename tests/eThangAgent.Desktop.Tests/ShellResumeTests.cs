using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>Shell-level resume behavior: resuming a persisted session opens a tab whose
///     transcript replays the session's conversation; resuming an already-open session
///     selects its tab instead of double-resuming; a host without a factory degrades to
///     a structured failure. Session creation is faked at the factory seam — the real
///     resume path is covered in Composition tests and the E2E suite.</summary>
public class ShellResumeTests
{
  private static readonly AgentId SessionId = AgentId.NewId();

  private static AgentSession SeededSession(string root, AgentId rootId, params Message[] messages)
  {
    // A session whose container is never disposed (no ServiceProvider) — tests that
    // close tabs must not touch Services. Build via a throwaway ServiceCollection so
    // DisposeAsync stays legal.
    ServiceProvider services = new ServiceCollection().BuildServiceProvider();
    return new AgentSession(
        services,
        rootId,
        new Conversation(messages),
        Handler: null!,
        Lifecycle: new RootSessionLifecycle(new TestFixtures.StubStore()),
        Model: ModelConfig.Create("test/model", null, 128, 0.1f).Value!,
        WorkspaceRoot: root,
        ProviderName: "openrouter",
        ClarifyChannel: null!,
        Inbox: new AgentInbox(),
        ChildRuntime: new TestFixtures.StubAgentRuntime(),
        Preferences: null);
  }

  private static MainViewModel ShellReturningPerRoot(params AgentSession[] sessions)
  {
    Dictionary<string, AgentSession> byRoot = sessions.GroupBy(s => s.WorkspaceRoot)
        .Select(g => g.Last()).ToDictionary(s => s.WorkspaceRoot);
    return new MainViewModel(
        (root, _) => Task.FromResult(Result.Success(byRoot[root])));
  }

  [Fact]
  public async Task ResumeSessionAsync_Without_Factory_Fails_Structured()
  {
    MainViewModel vm = new(
        (_, _) => Task.FromResult(Result.Failure<AgentSession>(new DomainError("NoFactory", "unused"))));

    Result<AgentTabViewModel> result = await vm.ResumeSessionAsync(SessionId);

    Assert.False(result.IsSuccess);
    Assert.Equal("ResumeUnavailable", result.Error!.Code);
    Assert.Empty(vm.Tabs);
  }

  [Fact]
  public async Task ResumeSessionAsync_AlreadyOpen_Selects_The_Tab()
  {
    // ForPrebuiltSessionAsync is the production-open path for a prebuilt session.
    AgentSession session = SeededSession(@"C:\ws\demo", SessionId,
        new Message(Role.User, "earlier", DateTimeOffset.UtcNow));
    MainViewModel shell = await MainViewModel.ForPrebuiltSessionAsync(session);

    Result<AgentTabViewModel> resumed = await shell.ResumeSessionAsync(SessionId);

    Assert.True(resumed.IsSuccess);
    Assert.Same(shell.Tabs.Single(), resumed.Value);
    Assert.Same(shell.Tabs.Single(), shell.SelectedTab);
    _ = Assert.Single(shell.Tabs); // selected, not double-resumed into a second tab
  }

  [Fact]
  public async Task AttachSession_Replays_The_Conversation_Into_The_Transcript()
  {
    AgentSession session = SeededSession(@"C:\ws\demo", SessionId,
        new Message(Role.User, "persisted question", DateTimeOffset.UtcNow),
        new Message(Role.Assistant, "persisted answer", DateTimeOffset.UtcNow));

    MainViewModel shell = await MainViewModel.ForPrebuiltSessionAsync(session);
    TranscriptViewModel transcript = shell.Tabs[0].ViewModel.Transcript;

    Assert.Equal(2, transcript.Entries.Count);
    Assert.Equal("persisted question", Assert.IsType<UserMessageEntry>(transcript.Entries[0]).Text);
    Assert.Equal("persisted answer", Assert.IsType<AssistantTextEntry>(transcript.Entries[1]).Text);
  }

  [Fact]
  public void OpenSessionsCommand_Raises_Dialog_Request()
  {
    MainViewModel vm = new(
        (_, _) => Task.FromResult(Result.Failure<AgentSession>(new DomainError("NoFactory", "unused"))));
    bool raised = false;
    vm.SessionsRequested += (_, _) => raised = true;

    vm.OpenSessionsCommand.Execute(null);

    Assert.True(raised);
  }

  [Fact]
  public async Task OpenSessionIds_Lists_Every_Open_Tab_Root()
  {
    AgentSession first = SeededSession(@"C:\ws\a", AgentId.NewId());
    AgentSession second = SeededSession(@"C:\ws\b", AgentId.NewId());
    MainViewModel shell = ShellReturningPerRoot(first, second);
    _ = await shell.OpenAgentAsync(@"C:\ws\a", "openrouter").ConfigureAwait(true);
    _ = await shell.OpenAgentAsync(@"C:\ws\b", "openrouter").ConfigureAwait(true);

    Assert.Equal(2, shell.Tabs.Count);
    Assert.Equal(2, shell.OpenSessionIds.Count);
    Assert.Contains(first.RootId, shell.OpenSessionIds);
    Assert.Contains(second.RootId, shell.OpenSessionIds);
  }
}
