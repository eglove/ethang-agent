using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>The persistence side of the skill-directory surface (skill-routing
///     Phase 1, Task 10 - the dialog rows moved to NewAgentLaunchFilesTests with
///     the launch surface): a confirmed launch choice persists through
///     SkillDirectoryPreferences.Serialize to the matching keys (DeleteAsync on an
///     empty list), a shell without a preference store persists as a silent
///     no-op, and the prefill loaders read the same keys back.</summary>
public class LaunchSkillDirectoriesPersistenceTests
{
  private const string WsRoot = @"C:\work\skill-directories";

  // ── load: the store's lists prefill; unset is empty ──

  [Fact]
  public async Task Load_Empty_Global_And_Workspace()
  {
    FakePreferenceStore store = new();
    MainViewModel shell = CreateSettingsShell(store);
    _ = await OpenShellAsync(shell, WsRoot);

    Assert.Empty(await shell.GetGlobalSkillDirectoriesAsync());
    Assert.Empty(await shell.GetWorkspaceSkillDirectoriesAsync(WsRoot));
  }

  [Fact]
  public async Task Configured_Entries_Load_With_Their_Checkbox_State()
  {
    FakePreferenceStore store = new();
    store.Stored[SkillDirectoryPreferences.GlobalKey] = SkillDirectoryPreferences.Serialize(
        [new SessionFileEntry(@"C:\skills\global", true)]);
    store.Stored[SkillDirectoryPreferences.WorkspaceKey(WsRoot)] = SkillDirectoryPreferences.Serialize(
        [new SessionFileEntry(@"C:\skills\ws", false)]);
    MainViewModel shell = CreateSettingsShell(store);
    _ = await OpenShellAsync(shell, WsRoot);

    SessionFileEntry globalEntry = Assert.Single(await shell.GetGlobalSkillDirectoriesAsync());
    Assert.Equal(@"C:\skills\global", globalEntry.Path);
    Assert.True(globalEntry.Enabled);
    SessionFileEntry workspaceEntry = Assert.Single(await shell.GetWorkspaceSkillDirectoriesAsync(WsRoot));
    Assert.Equal(@"C:\skills\ws", workspaceEntry.Path);
    Assert.False(workspaceEntry.Enabled);
  }

  // ── save: the persisted keys and JSON, through the launch choice ──

  [Fact]
  public async Task LaunchChoice_WritesKeyAndJson()
  {
    FakePreferenceStore store = new();
    MainViewModel shell = CreateSettingsShell(store);

    await shell.ApplyLaunchFilesAsync(new NewAgentChoice("openrouter", WsRoot,
        [], [], [new SessionFileEntry(@"C:\skills\global", true)], []));

    string expected = SkillDirectoryPreferences.Serialize(
        [new SessionFileEntry(@"C:\skills\global", true)]);
    Assert.Equal(expected, store.Stored[SkillDirectoryPreferences.GlobalKey]);
    SessionFileEntry reloaded = Assert.Single(await shell.GetGlobalSkillDirectoriesAsync());
    Assert.Equal(@"C:\skills\global", reloaded.Path);
    Assert.True(reloaded.Enabled);
  }

  [Fact]
  public async Task LaunchChoice_WorkspaceScope_UsesWorkspaceKey()
  {
    FakePreferenceStore store = new();
    MainViewModel shell = CreateSettingsShell(store);

    await shell.ApplyLaunchFilesAsync(new NewAgentChoice("openrouter", WsRoot,
        [], [], [], [new SessionFileEntry(@"C:\skills\ws", true)]));

    string expected = SkillDirectoryPreferences.Serialize(
        [new SessionFileEntry(@"C:\skills\ws", true)]);
    Assert.Equal(expected, store.Stored[SkillDirectoryPreferences.WorkspaceKey(WsRoot)]);
    Assert.False(store.Stored.ContainsKey(SkillDirectoryPreferences.GlobalKey));
    SessionFileEntry reloaded = Assert.Single(await shell.GetWorkspaceSkillDirectoriesAsync(WsRoot));
    Assert.Equal(@"C:\skills\ws", reloaded.Path);
  }

  [Fact]
  public async Task LaunchChoice_EmptyList_Deletes_The_Key()
  {
    FakePreferenceStore store = new();
    store.Stored[SkillDirectoryPreferences.GlobalKey] = SkillDirectoryPreferences.Serialize(
        [new SessionFileEntry(@"C:\skills\global", true)]);
    MainViewModel shell = CreateSettingsShell(store);

    await shell.ApplyLaunchFilesAsync(new NewAgentChoice("openrouter", WsRoot,
        [], [], [], []));

    // An empty list DELETEs the preference — never an empty-array value.
    Assert.Contains(SkillDirectoryPreferences.GlobalKey, store.Deletions);
    Assert.False(store.Stored.ContainsKey(SkillDirectoryPreferences.GlobalKey));
    Assert.Empty(await shell.GetGlobalSkillDirectoriesAsync());
  }

  [Fact]
  public async Task NullPreferences_LaunchChoiceIsNoop_NoThrow()
  {
    MainViewModel shell = CreateSettingsShell(preferences: null);

    Exception? failure = await Record.ExceptionAsync(() => shell.ApplyLaunchFilesAsync(
        new NewAgentChoice("openrouter", WsRoot,
            [], [], [new SessionFileEntry(@"C:\skills\global", true)],
            [new SessionFileEntry(@"C:\skills\ws", true)])));

    Assert.Null(failure);
    Assert.Empty(await shell.GetGlobalSkillDirectoriesAsync());
    Assert.Empty(await shell.GetWorkspaceSkillDirectoriesAsync(WsRoot));
  }

  // ── helpers (the EffortSettingsPersistenceTests shell shape: a faked session
  //    delegate for tab opens plus Settings + factory so ApplySettingsAsync persists) ──

  private static MainViewModel CreateSettingsShell(IAppPreferenceStore? preferences)
      => new((root, provider) => Task.FromResult(Result.Success(BuildSession(root, provider))),
          new MainViewModelOptions
          {
            Preferences = preferences,
            Settings = Settings(),
            SessionFactory = new AgentSessionFactory(Settings()),
          });

  private static AgentSettings Settings() => new(
      new OpenRouterSettings(null, new Uri("https://openrouter.test")),
      new AgentDomain.SubAgentOptions(null, 2));

  private static async Task<AgentTabViewModel> OpenShellAsync(MainViewModel shell, string root)
  {
    Result<AgentTabViewModel> opened = await shell.OpenAgentAsync(root, "openrouter").ConfigureAwait(true);
    Assert.True(opened.IsSuccess, $"session open failed: [{opened.Error?.Code}] {opened.Error?.Message}");
    return opened.Value;
  }

  private static AgentSession BuildSession(string root, string provider)
  {
    // A session whose container these tests never dispose — built via a throwaway
    // ServiceCollection so DisposeAsync stays legal (the shell-test shape).
    ServiceProvider services = new ServiceCollection().BuildServiceProvider();
    return new AgentSession(
        services,
        AgentDomain.AgentId.NewId(),
        new ConversationDomain.Conversation(),
        Handler: null!,
        Lifecycle: new RootSessionLifecycle(new TestFixtures.StubStore()),
        Model: ModelConfig.Create("test/model", null, 128, 0.1f, 8192).Value!,
        WorkspaceRoot: root,
        ProviderName: provider,
        Inbox: new AgentDomain.BoundedAgentMailbox(),
        ChildRuntime: new TestFixtures.StubAgentRuntime(),
        Preferences: null);
  }

  private sealed class FakePreferenceStore : IAppPreferenceStore
  {
    public List<(string Key, string Value)> Writes { get; } = [];

    public List<string> Deletions { get; } = [];

    public Dictionary<string, string> Stored { get; } = [];

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => Task.FromResult(Stored.TryGetValue(key, out string? value) ? value : null);

    public Task<bool> SetAsync(string key, string value, CancellationToken ct = default)
    {
      Writes.Add((key, value));
      Stored[key] = value;
      return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
      Deletions.Add(key);
      _ = Stored.Remove(key);
      return Task.FromResult(true);
    }
  }
}
