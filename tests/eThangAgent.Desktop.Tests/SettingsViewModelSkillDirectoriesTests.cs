using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>The persistence side of the two skill-directory scopes: the GLOBAL list lives in the
///     app Settings window and persists through <see cref="MainViewModel.ApplySettingsAsync"/>,
///     the WORKSPACE list lives in the workspace settings window and persists through
///     <see cref="MainViewModel.ApplyWorkspaceSettingsAsync"/>. Both delete their key on an
///     empty list, and both read back through the same loaders the windows prefill from.</summary>
[Trait("Category", "Process")]
public class SkillDirectoryScopePersistenceTests
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

  // ── save: the GLOBAL list, through the app settings save ──

  [Fact]
  public async Task AppSettings_WritesTheGlobalKeyAndJson()
  {
    FakePreferenceStore store = new();
    MainViewModel shell = CreateSettingsShell(store);

    await shell.ApplySettingsAsync(new SettingsUpdate(null, CommitStyle.Conventional,
        GlobalSkillDirectories: [new SessionFileEntry(@"C:\skills\global", true)]));

    string expected = SkillDirectoryPreferences.Serialize(
        [new SessionFileEntry(@"C:\skills\global", true)]);
    Assert.Equal(expected, store.Stored[SkillDirectoryPreferences.GlobalKey]);
    SessionFileEntry reloaded = Assert.Single(await shell.GetGlobalSkillDirectoriesAsync());
    Assert.Equal(@"C:\skills\global", reloaded.Path);
    Assert.True(reloaded.Enabled);
  }

  [Fact]
  public async Task AppSettings_EmptyGlobalList_DeletesTheKey()
  {
    FakePreferenceStore store = new();
    store.Stored[SkillDirectoryPreferences.GlobalKey] = SkillDirectoryPreferences.Serialize(
        [new SessionFileEntry(@"C:\skills\global", true)]);
    MainViewModel shell = CreateSettingsShell(store);

    await shell.ApplySettingsAsync(new SettingsUpdate(null, CommitStyle.Conventional,
        GlobalSkillDirectories: []));

    // An empty list DELETEs the preference — never an empty-array value.
    Assert.Contains(SkillDirectoryPreferences.GlobalKey, store.Deletions);
    Assert.False(store.Stored.ContainsKey(SkillDirectoryPreferences.GlobalKey));
    Assert.Empty(await shell.GetGlobalSkillDirectoriesAsync());
  }

  // ── save: the GLOBAL session-file list, through the same app settings save ──

  [Fact]
  public async Task AppSettings_WritesTheGlobalSessionFileKeyAndJson()
  {
    FakePreferenceStore store = new();
    MainViewModel shell = CreateSettingsShell(store);

    await shell.ApplySettingsAsync(new SettingsUpdate(null, CommitStyle.Conventional,
        GlobalSessionFiles: [new SessionFileEntry(@"C:\g\AGENTS.md", true)]));

    string expected = SessionFilePreferences.Serialize(
        [new SessionFileEntry(@"C:\g\AGENTS.md", true)]);
    Assert.Equal(expected, store.Stored[SessionFilePreferences.GlobalKey]);
    SessionFileEntry reloaded = Assert.Single(await shell.GetGlobalSessionFilesAsync());
    Assert.Equal(@"C:\g\AGENTS.md", reloaded.Path);
    Assert.True(reloaded.Enabled);
  }

  [Fact]
  public async Task AppSettings_EmptyGlobalSessionFileList_DeletesTheKey()
  {
    FakePreferenceStore store = new();
    store.Stored[SessionFilePreferences.GlobalKey] = SessionFilePreferences.Serialize(
        [new SessionFileEntry(@"C:\g\AGENTS.md", true)]);
    MainViewModel shell = CreateSettingsShell(store);

    await shell.ApplySettingsAsync(new SettingsUpdate(null, CommitStyle.Conventional,
        GlobalSessionFiles: []));

    Assert.Contains(SessionFilePreferences.GlobalKey, store.Deletions);
    Assert.False(store.Stored.ContainsKey(SessionFilePreferences.GlobalKey));
    Assert.Empty(await shell.GetGlobalSessionFilesAsync());
  }

  // ── save: the WORKSPACE list, through the workspace settings save ──

  [Fact]
  public async Task WorkspaceSettings_WritesTheWorkspaceKeyAndJson()
  {
    FakePreferenceStore store = new();
    MainViewModel shell = CreateSettingsShell(store);
    _ = await OpenShellAsync(shell, WsRoot);

    Result<bool> applied = await shell.ApplyWorkspaceSettingsAsync(new WorkspaceSettingsSave(WsRoot,
        [], [new SessionFileEntry(@"C:\skills\ws", true)], []));
    Assert.True(applied.IsSuccess);

    string expected = SkillDirectoryPreferences.Serialize(
        [new SessionFileEntry(@"C:\skills\ws", true)]);
    Assert.Equal(expected, store.Stored[SkillDirectoryPreferences.WorkspaceKey(WsRoot)]);
    Assert.False(store.Stored.ContainsKey(SkillDirectoryPreferences.GlobalKey));
    SessionFileEntry reloaded = Assert.Single(await shell.GetWorkspaceSkillDirectoriesAsync(WsRoot));
    Assert.Equal(@"C:\skills\ws", reloaded.Path);
  }

  [Fact]
  public async Task WorkspaceSettings_WritesTheWorkspaceSessionFileKeyAndJson()
  {
    FakePreferenceStore store = new();
    MainViewModel shell = CreateSettingsShell(store);
    _ = await OpenShellAsync(shell, WsRoot);

    Result<bool> applied = await shell.ApplyWorkspaceSettingsAsync(new WorkspaceSettingsSave(WsRoot,
        [new SessionFileEntry("AGENTS.md", true)], [], []));
    Assert.True(applied.IsSuccess);

    string expected = SessionFilePreferences.Serialize([new SessionFileEntry("AGENTS.md", true)]);
    Assert.Equal(expected, store.Stored[SessionFilePreferences.WorkspaceKey(WsRoot)]);
    Assert.False(store.Stored.ContainsKey(SessionFilePreferences.GlobalKey));
    SessionFileEntry reloaded = Assert.Single(await shell.GetWorkspaceSessionFilesAsync(WsRoot));
    Assert.Equal("AGENTS.md", reloaded.Path);
  }

  [Fact]
  public async Task WorkspaceSettings_EmptyList_DeletesTheKey()
  {
    FakePreferenceStore store = new();
    store.Stored[SkillDirectoryPreferences.WorkspaceKey(WsRoot)] = SkillDirectoryPreferences.Serialize(
        [new SessionFileEntry(@"C:\skills\ws", true)]);
    MainViewModel shell = CreateSettingsShell(store);
    _ = await OpenShellAsync(shell, WsRoot);

    Result<bool> applied = await shell.ApplyWorkspaceSettingsAsync(new WorkspaceSettingsSave(WsRoot,
        [], [], []));
    Assert.True(applied.IsSuccess);

    Assert.Contains(SkillDirectoryPreferences.WorkspaceKey(WsRoot), store.Deletions);
    Assert.False(store.Stored.ContainsKey(SkillDirectoryPreferences.WorkspaceKey(WsRoot)));
    Assert.Empty(await shell.GetWorkspaceSkillDirectoriesAsync(WsRoot));
  }

  [Fact]
  public async Task WorkspaceSettings_ForeignRoot_FailsNamed_AndPersistsNothing()
  {
    FakePreferenceStore store = new();
    MainViewModel shell = CreateSettingsShell(store, boundRoot: WsRoot);

    Result<bool> applied = await shell.ApplyWorkspaceSettingsAsync(new WorkspaceSettingsSave(
        @"C:\work\other", [new SessionFileEntry("AGENTS.md", true)], [], []));

    Assert.False(applied.IsSuccess);
    Assert.Equal("ForeignWorkspace", applied.Error.Code);
    Assert.Empty(store.Writes);
    Assert.Empty(store.Deletions);
  }

  [Fact]
  public async Task WorkspaceSettings_BlankRoot_FailsNamed_AndPersistsNothing()
  {
    FakePreferenceStore store = new();
    MainViewModel shell = CreateSettingsShell(store);

    Result<bool> applied = await shell.ApplyWorkspaceSettingsAsync(new WorkspaceSettingsSave(
        "   ", [], [], []));

    Assert.False(applied.IsSuccess);
    Assert.Equal("InvalidWorkspace", applied.Error.Code);
    Assert.Empty(store.Writes);
    Assert.Empty(store.Deletions);
  }

  [Fact]
  public async Task NullPreferences_WorkspaceSettingsIsNoop_NoThrow()
  {
    MainViewModel shell = CreateSettingsShell(preferences: null);

    Exception? failure = await Record.ExceptionAsync(() => shell.ApplyWorkspaceSettingsAsync(
        new WorkspaceSettingsSave(WsRoot, [new SessionFileEntry("AGENTS.md", true)],
            [new SessionFileEntry(@"C:\skills\ws", true)], []))).ConfigureAwait(true);

    Assert.Null(failure);
    Assert.Empty(await shell.GetGlobalSkillDirectoriesAsync());
    Assert.Empty(await shell.GetWorkspaceSkillDirectoriesAsync(WsRoot));
  }

  // ── the two scopes are independent: a global list stays empty while a workspace list
  //    holds a row, and the trusted-directory key is deleted by an empty save ──

  [Fact]
  public async Task GlobalEmpty_WorkspaceHoldingAGENTSMD_ReadsAsOneEnabledWorkspaceRow()
  {
    FakePreferenceStore store = new();
    store.Stored[SessionFilePreferences.WorkspaceKey(WsRoot)] =
        SessionFilePreferences.Serialize([new SessionFileEntry("AGENTS.md", true)]);
    MainViewModel shell = CreateSettingsShell(store, boundRoot: WsRoot);

    // The app Settings window's two lists read as empty: nothing applies to every workspace.
    Assert.Empty(await shell.GetGlobalSessionFilesAsync());
    Assert.Empty(await shell.GetGlobalSkillDirectoriesAsync());

    // The workspace settings window reads one enabled row for this workspace.
    SessionFileEntry row = Assert.Single(await shell.GetWorkspaceSessionFilesAsync());
    Assert.Equal("AGENTS.md", row.Path);
    Assert.True(row.Enabled);
  }

  [Fact]
  public async Task WorkspaceSettings_EmptyTrustedList_DeletesTheWorkspaceKey()
  {
    FakePreferenceStore store = new();
    string key = TrustedDirectoryPreferences.WorkspaceKey(WsRoot);
    store.Stored[key] = TrustedDirectoryPreferences.Serialize(
        [new TrustedDirectoryEntry(@"D:\shared", true, false)]);
    MainViewModel shell = CreateSettingsShell(store, boundRoot: WsRoot);

    Result<bool> applied = await shell.ApplyWorkspaceSettingsAsync(new WorkspaceSettingsSave(
        WsRoot, [], [], []));

    Assert.True(applied.IsSuccess);
    Assert.Contains(key, store.Deletions);
    Assert.False(store.Stored.ContainsKey(key));
    Assert.Empty(await shell.GetTrustedDirectoriesAsync());
  }

  [Fact]
  public async Task WorkspaceSettings_WritesTheTrustedKeyAndJson()
  {
    FakePreferenceStore store = new();
    MainViewModel shell = CreateSettingsShell(store, boundRoot: WsRoot);

    Result<bool> applied = await shell.ApplyWorkspaceSettingsAsync(new WorkspaceSettingsSave(
        WsRoot, [], [], [new TrustedDirectoryEntry(@"D:\shared", true, true)]));

    Assert.True(applied.IsSuccess);
    string key = TrustedDirectoryPreferences.WorkspaceKey(WsRoot);
    Assert.Equal(TrustedDirectoryPreferences.Serialize(
            [new TrustedDirectoryEntry(@"D:\shared", true, true)]),
        store.Stored[key]);
    TrustedDirectoryEntry reloaded = Assert.Single(await shell.GetTrustedDirectoriesAsync());
    Assert.True(reloaded.Editable);
  }

  // ── helpers (the EffortSettingsPersistenceTests shell shape: a faked session
  //    delegate for tab opens plus Settings + factory so ApplySettingsAsync persists) ──

  private static MainViewModel CreateSettingsShell(IAppPreferenceStore? preferences,
      string? boundRoot = null)
      => new((root, provider) => Task.FromResult(Result.Success(BuildSession(root, provider))),
          new MainViewModelOptions
          {
            Preferences = preferences,
            WorkspaceRoot = boundRoot,
            Settings = Settings(),
            SessionFactory = new AgentSessionFactory(Settings()),
          });

  private static AgentSettings Settings() => new(
      new OpenRouterSettings(null, new Uri("https://openrouter.test")),
      new AgentDomain.AgentRunOptions(null, 2));

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
        Model: ModelConfig.Create("test/model", 128, 0.1f, 8192).Value!,
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
