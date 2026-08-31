using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>Shell lifecycle hooks for the watchdog host: opening a tab fires SessionOpened
///     with the session; closing fires SessionClosed with the root id; null hooks are safe.
///     Session creation is faked at the createSession seam (ShellResumeTests pattern).</summary>
public class WatchdogLifecycleTests
{
  private static AgentSession SeededSession(string root, AgentId rootId)
  {
    ServiceProvider services = new ServiceCollection().BuildServiceProvider();
    return new AgentSession(
        services,
        rootId,
        new Conversation(),
        Handler: null!,
        Lifecycle: new RootSessionLifecycle(new TestFixtures.StubStore()),
        Model: ModelConfig.Create("test/model", null, 128, 0.1f, 8192).Value!,
        WorkspaceRoot: root,
        ProviderName: "openrouter",
        ClarifyChannel: null!,
        Inbox: new AgentInbox(),
        ChildRuntime: new TestFixtures.StubAgentRuntime(),
        Preferences: null);
  }

  [Fact]
  public async Task OpeningTab_InvokesSessionOpened_WithTheSession()
  {
    AgentId rootId = AgentId.NewId();
    AgentId? seen = null;
    MainViewModel vm = new(
        (_, _) => Task.FromResult(Result.Success(SeededSession(@"C:\ws\watch", rootId))),
        new MainViewModelOptions { SessionOpened = s => seen = s.RootId });

    Result<AgentTabViewModel> opened = await vm.OpenAgentAsync(@"C:\ws\watch", "openrouter");

    Assert.True(opened.IsSuccess);
    Assert.Equal(rootId, seen);
  }

  [Fact]
  public async Task ClosingTab_InvokesSessionClosed_WithRootId()
  {
    AgentId rootId = AgentId.NewId();
    AgentId? closed = null;
    MainViewModel vm = new(
        (_, _) => Task.FromResult(Result.Success(SeededSession(@"C:\ws\watch", rootId))),
        new MainViewModelOptions { SessionClosed = id => closed = id });

    Result<AgentTabViewModel> opened = await vm.OpenAgentAsync(@"C:\ws\watch", "openrouter");
    Assert.True(opened.IsSuccess);

    await vm.CloseTabAsync(vm.Tabs.Single());

    Assert.Equal(rootId, closed);
  }

  [Fact]
  public async Task NullHooks_AreSafe()
  {
    MainViewModel vm = new(
        (_, _) => Task.FromResult(Result.Success(SeededSession(@"C:\ws\watch", AgentId.NewId()))),
        new MainViewModelOptions());

    Result<AgentTabViewModel> opened = await vm.OpenAgentAsync(@"C:\ws\watch", "openrouter");
    Assert.True(opened.IsSuccess);

    await vm.CloseTabAsync(vm.Tabs.Single());
  }
}
