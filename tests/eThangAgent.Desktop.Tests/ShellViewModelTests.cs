using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>Shell-level behavior of the main window: menu bar, tab lifecycle, the
///     empty state, and the provider dimension of tabs. Session creation is faked at
///     the factory seam — the factory's own composition is covered in
///     eThangAgent.Composition.Tests.</summary>
public class ShellViewModelTests
{
  private static MainViewModel CreateShell(
      Func<string, string, AgentSession>? factory = null, IAppPreferenceStore? preferences = null)
  {
    Task<Result<AgentSession>> create(string root, string provider)
    {
      if (factory is null)
      {
        return Task.FromResult(Result.Failure<AgentSession>(new DomainError("NoFactory", "no session factory configured")));
      }

      AgentSession session = factory(root, provider);
      return Task.FromResult(Result.Success(session));
    }
    return new MainViewModel(create, preferences: preferences);
  }

  private static AgentSession FakeSession(string root, string provider = "openrouter")
  {
    // A session whose container is never disposed (no ServiceProvider) — tests that
    // close tabs must not touch Services. Build via a throwaway ServiceCollection so
    // DisposeAsync stays legal.
    ServiceProvider services = new ServiceCollection()
        .BuildServiceProvider();
    return new AgentSession(
        services,
        AgentDomain.AgentId.NewId(),
        new ConversationDomain.Conversation(),
        Handler: null!,
        Lifecycle: new RootSessionLifecycle(new TestFixtures.StubStore()),
        Model: ModelDomain.ModelConfig.Create("test/model", null, 128, 0.1f).Value!,
        WorkspaceRoot: root,
        ProviderName: provider,
        ClarifyChannel: null!,
        Inbox: new AgentDomain.AgentInbox(),
        ChildRuntime: new TestFixtures.StubAgentRuntime());
  }

  private sealed class FakePreferenceStore : IAppPreferenceStore
  {
    public List<(string Key, string Value)> Writes { get; } = [];

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task<bool> SetAsync(string key, string value, CancellationToken ct = default)
    {
      Writes.Add((key, value));
      return Task.FromResult(true);
    }
  }

  [Fact]
  public void OpenAgentCommand_Raises_Dialog_Request()
  {
    MainViewModel vm = CreateShell();
    bool raised = false;
    vm.OpenAgentRequested += (_, _) => raised = true;

    vm.OpenAgentCommand.Execute(null);

    Assert.True(raised);
  }

  [Fact]
  public async Task OpenAgent_Creates_Tab_Selects_It_And_Reports_HasTabs()
  {
    MainViewModel vm = CreateShell((root, _) => FakeSession(root));

    Result<AgentTabViewModel> opened = await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter");

    Assert.True(opened.IsSuccess);
    _ = Assert.Single(vm.Tabs);
    Assert.Equal(vm.Tabs[0], vm.SelectedTab);
    Assert.True(vm.HasTabs);
    Assert.Equal("alpha", opened.Value!.Title);
  }

  [Fact]
  public async Task OpenAgent_BindsTabToItsProvider_ForTheStatusAndViewModel()
  {
    MainViewModel vm = CreateShell((root, provider) => FakeSession(root, provider));

    _ = await vm.OpenAgentAsync(@"C:\work\alpha", "zai");

    Assert.Equal("z.ai", vm.Tabs[0].ViewModel.Status.Provider);
  }

  [Fact]
  public async Task Opening_Same_Directory_Provider_Twice_Selects_Existing_Tab()
  {
    MainViewModel vm = CreateShell((root, _) => FakeSession(root));
    _ = await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter");

    Result<AgentTabViewModel> second = await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter");

    _ = Assert.Single(vm.Tabs);
    Assert.Equal(vm.Tabs[0], vm.SelectedTab);
    Assert.Equal(vm.Tabs[0], second.Value);
  }

  [Fact]
  public async Task Opening_Same_Directory_Under_Both_Providers_Opens_Two_Tabs()
  {
    // Provider is part of tab identity: one workspace may run under both providers
    // concurrently — they share workspace-scoped state by design.
    MainViewModel vm = CreateShell((root, provider) => FakeSession(root, provider));

    _ = await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter");
    _ = await vm.OpenAgentAsync(@"C:\work\alpha", "zai");

    Assert.Equal(2, vm.Tabs.Count);
    Assert.Equal("z.ai", vm.Tabs[1].ViewModel.Status.Provider);
  }

  [Fact]
  public async Task Opening_A_Second_Directory_Adds_Another_Tab()
  {
    MainViewModel vm = CreateShell((root, _) => FakeSession(root));
    _ = await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter");
    _ = await vm.OpenAgentAsync(@"C:\work\beta", "openrouter");

    Assert.Equal(2, vm.Tabs.Count);
    Assert.Equal("beta", vm.SelectedTab!.Title); // newest tab selected
  }

  [Fact]
  public async Task CloseTab_Removes_Tab_And_Falls_Back_Selection_To_Last()
  {
    MainViewModel vm = CreateShell((root, _) => FakeSession(root));
    AgentTabViewModel alpha = (await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter")).Value!;
    AgentTabViewModel beta = (await vm.OpenAgentAsync(@"C:\work\beta", "openrouter")).Value!;
    Assert.Equal(beta, vm.SelectedTab);

    await vm.CloseTabAsync(beta);

    Assert.DoesNotContain(beta, vm.Tabs);
    Assert.Equal(alpha, vm.SelectedTab);
    Assert.True(vm.HasTabs);
  }

  [Fact]
  public async Task Closing_Last_Tab_Returns_To_Empty_Shell()
  {
    MainViewModel vm = CreateShell((root, _) => FakeSession(root));
    AgentTabViewModel alpha = (await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter")).Value!;

    await vm.CloseTabAsync(alpha);

    Assert.Empty(vm.Tabs);
    Assert.False(vm.HasTabs);
    Assert.Null(vm.SelectedTab);
  }

  [Fact]
  public async Task OpenAgent_Failure_Surfaces_Structured_Error_And_No_Tab()
  {
    MainViewModel vm = CreateShell(); // factory fails every request

    Result<AgentTabViewModel> opened = await vm.OpenAgentAsync(@"C:\work\gamma", "openrouter");

    Assert.False(opened.IsSuccess);
    Assert.Equal("NoFactory", opened.Error!.Code);
    Assert.Empty(vm.Tabs);
  }

  [Fact]
  public async Task OpenAgent_PersistsChosenProvider_AsAppPreference()
  {
    FakePreferenceStore preferences = new();
    MainViewModel vm = CreateShell((root, provider) => FakeSession(root, provider), preferences);

    _ = await vm.OpenAgentAsync(@"C:\work\alpha", "zai");

    (string key, string value) = Assert.Single(preferences.Writes);
    Assert.Equal(Providers.PreferenceKey, key);
    Assert.Equal("zai", value);
  }
}
