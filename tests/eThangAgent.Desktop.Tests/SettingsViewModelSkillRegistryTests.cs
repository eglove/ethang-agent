using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>The skill-registry target row (plan #29 task 12): tri-state
///     (unset/global/workspace); save persists through the preference store
///     (delete on unset); the loader strict-parses on read.</summary>
public class SettingsViewModelSkillRegistryTests
{
  private const string WsRoot = @"C:\work\skill-registry";

  [Fact]
  public async Task Default_Target_Is_Unset()
  {
    FakeStore store = new();
    MainViewModel shell = CreateShell(store);
    _ = await OpenShellAsync(shell, WsRoot);
    Assert.Equal(string.Empty, await shell.GetSkillRegistryDefaultTargetAsync());
  }

  [Fact]
  public async Task Save_Global_Persists()
  {
    FakeStore store = new();
    MainViewModel shell = CreateShell(store);
    _ = await OpenShellAsync(shell, WsRoot);
    await shell.ApplySettingsAsync(MinimalUpdate() with { SkillRegistryDefaultTarget = "global" });
    Assert.Equal("global", store.Stored[AgentPreferenceKeys.SkillRegistryDefaultTarget]);
  }

  [Fact]
  public async Task Save_Workspace_Persists()
  {
    FakeStore store = new();
    MainViewModel shell = CreateShell(store);
    _ = await OpenShellAsync(shell, WsRoot);
    await shell.ApplySettingsAsync(MinimalUpdate() with { SkillRegistryDefaultTarget = "workspace" });
    Assert.Equal("workspace", store.Stored[AgentPreferenceKeys.SkillRegistryDefaultTarget]);
  }

  [Fact]
  public async Task Save_Unset_Deletes_The_Key()
  {
    FakeStore store = new();
    store.Stored[AgentPreferenceKeys.SkillRegistryDefaultTarget] = "global";
    MainViewModel shell = CreateShell(store);
    _ = await OpenShellAsync(shell, WsRoot);
    await shell.ApplySettingsAsync(MinimalUpdate() with { SkillRegistryDefaultTarget = string.Empty });
    Assert.False(store.Stored.ContainsKey(AgentPreferenceKeys.SkillRegistryDefaultTarget));
  }

  [Fact]
  public void Dialog_Round_Trips_The_Selection()
  {
    SettingsViewModel vm = CreateDialog("workspace");
    Assert.Equal("workspace", vm.SkillRegistryTarget);
    vm.SkillRegistryTarget = "global";
    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;
    vm.SaveCommand.Execute(null);
    Assert.NotNull(saved);
    Assert.Equal("global", saved.SkillRegistryDefaultTarget);
  }

  [Fact]
  public void Dialog_Unset_Renders_Empty()
  {
    SettingsViewModel vm = CreateDialog(null);
    Assert.Equal(string.Empty, vm.SkillRegistryTarget);
  }

  // ── helpers (the SettingsViewModelSkillDirectoriesTests shell shape) ──

  private static SettingsViewModel CreateDialog(string? stored) => new(
      null, CommitStyle.Conventional,
      skillRegistryDefaultTarget: stored);

  private static SettingsUpdate MinimalUpdate() =>
      new(null, CommitStyle.Conventional);

  private static MainViewModel CreateShell(IAppPreferenceStore? preferences)
      => new((root, provider) => Task.FromResult(Result.Success(BuildSession(root, provider))),
          new MainViewModelOptions
          {
            Preferences = preferences,
            Settings = Settings(),
            SessionFactory = new AgentSessionFactory(Settings()),
          });

  private static AgentSettings Settings() => new(
      new OpenRouterSettings(null, new Uri("https://openrouter.test")),
      new SubAgentOptions(null, 2));

  private static async Task<AgentTabViewModel> OpenShellAsync(MainViewModel shell, string root)
  {
    Result<AgentTabViewModel> opened = await shell.OpenAgentAsync(root, "openrouter").ConfigureAwait(true);
    Assert.True(opened.IsSuccess, $"session open failed: [{opened.Error?.Code}] {opened.Error?.Message}");
    return opened.Value;
  }

  private static AgentSession BuildSession(string root, string provider)
  {
    ServiceProvider services = new ServiceCollection().BuildServiceProvider();
    return new AgentSession(
        services,
        AgentId.NewId(),
        new Conversation(),
        Handler: null!,
        Lifecycle: new RootSessionLifecycle(new TestFixtures.StubStore()),
        Model: ModelConfig.Create("test/model", null, 128, 0.1f, 8192).Value!,
        WorkspaceRoot: root,
        ProviderName: provider,
        Inbox: new BoundedAgentMailbox(),
        ChildRuntime: new TestFixtures.StubAgentRuntime(),
        Preferences: null);
  }

  private sealed class FakeStore : IAppPreferenceStore
  {
    public Dictionary<string, string> Stored { get; } = [];

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => Task.FromResult(Stored.TryGetValue(key, out string? value) ? value : null);

    public Task<bool> SetAsync(string key, string value, CancellationToken ct = default)
    {
      Stored[key] = value;
      return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
      _ = Stored.Remove(key);
      return Task.FromResult(true);
    }
  }
}
