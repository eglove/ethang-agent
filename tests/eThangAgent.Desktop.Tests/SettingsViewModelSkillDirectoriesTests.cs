using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>The settings surface for skill directories (skill-routing Phase 1,
///     Task 10): mirrors the session-files editor over the SkillDirectoryPreferences
///     keys — two scopes (global, workspace), checkbox enablement per row, add with
///     absolute-path validation, remove, and a save that persists through
///     SkillDirectoryPreferences.Serialize to the matching key (DeleteAsync on an
///     empty list — exactly the SessionFilePreferences usage the session-files
///     methods make). A shell without a preference store (headless host) persists
///     as a silent no-op.</summary>
public class SettingsViewModelSkillDirectoriesTests
{
  private const string WsRoot = @"C:\work\skill-directories";

  // ── load: the store's lists prefill both scopes; unset is empty ──

  [Fact]
  public async Task Load_Empty_Global_And_Workspace()
  {
    FakePreferenceStore store = new();
    MainViewModel shell = CreateSettingsShell(store);
    _ = await OpenShellAsync(shell, WsRoot);

    Assert.Empty(await shell.GetGlobalSkillDirectoriesAsync());
    Assert.Empty(await shell.GetWorkspaceSkillDirectoriesAsync());
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
    SessionFileEntry workspaceEntry = Assert.Single(await shell.GetWorkspaceSkillDirectoriesAsync());
    Assert.Equal(@"C:\skills\ws", workspaceEntry.Path);
    Assert.False(workspaceEntry.Enabled);
  }

  // ── the dialog's row editing mirrors the session-files surface ──

  [Fact]
  public void AddRow_Appends_Checked_And_Clears_The_Entry_Field()
  {
    SettingsViewModel vm = CreateDialog();
    vm.NewGlobalSkillDirectory = @"C:\skills\global";
    vm.AddGlobalSkillDirectoryCommand.Execute(null);
    SessionFileRow row = Assert.Single(vm.GlobalSkillDirectories);
    Assert.Equal(@"C:\skills\global", row.Path);
    Assert.True(row.Enabled);
    Assert.Equal(string.Empty, vm.NewGlobalSkillDirectory);
  }

  [Fact]
  public void Add_Rejects_Relative_Paths_With_A_Named_Error()
  {
    SettingsViewModel vm = CreateDialog();
    vm.NewGlobalSkillDirectory = "relative/skills";
    vm.AddGlobalSkillDirectoryCommand.Execute(null);
    Assert.Empty(vm.GlobalSkillDirectories);
    Assert.Contains("absolute", vm.ValidationError, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Without_A_Workspace_The_Workspace_Scope_Is_Inert()
  {
    SettingsViewModel vm = CreateDialog();
    Assert.False(vm.HasWorkspace);
    vm.NewWorkspaceSkillDirectory = @"C:\skills\ws";
    vm.AddWorkspaceSkillDirectoryCommand.Execute(null);
    Assert.Empty(vm.WorkspaceSkillDirectories);
    // Save carries no workspace directories: nothing may be persisted under a blank key.
    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;
    vm.SaveCommand.Execute(null);
    Assert.NotNull(saved);
    Assert.Null(saved.WorkspaceRoot);
    Assert.Null(saved.WorkspaceSkillDirectories);
  }

  [Fact]
  public void Remove_Deletes_Exactly_Its_Row()
  {
    SettingsViewModel vm = CreateDialog(global: [
        new SessionFileEntry(@"C:\skills\a", true),
        new SessionFileEntry(@"C:\skills\b", true)]);
    vm.RemoveGlobalSkillDirectoryCommand.Execute(vm.GlobalSkillDirectories[0]);
    SessionFileRow remaining = Assert.Single(vm.GlobalSkillDirectories);
    Assert.Equal(@"C:\skills\b", remaining.Path);
  }

  // ── save: the persisted keys and JSON, mirroring the SessionFilePreferences usage ──

  [Fact]
  public async Task AddRow_ThenSave_WritesKeyAndJson()
  {
    FakePreferenceStore store = new();
    MainViewModel shell = CreateSettingsShell(store);
    SettingsViewModel vm = CreateDialog();
    vm.NewGlobalSkillDirectory = @"C:\skills\global";
    vm.AddGlobalSkillDirectoryCommand.Execute(null);
    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;
    vm.SaveCommand.Execute(null);
    Assert.NotNull(saved);
    Assert.NotNull(saved.GlobalSkillDirectories);

    await shell.ApplySettingsAsync(saved);

    string expected = SkillDirectoryPreferences.Serialize(
        [new SessionFileEntry(@"C:\skills\global", true)]);
    Assert.Equal(expected, store.Stored[SkillDirectoryPreferences.GlobalKey]);
    SessionFileEntry reloaded = Assert.Single(await shell.GetGlobalSkillDirectoriesAsync());
    Assert.Equal(@"C:\skills\global", reloaded.Path);
    Assert.True(reloaded.Enabled);
  }

  [Fact]
  public async Task ToggleEnabled_Saves()
  {
    FakePreferenceStore store = new();
    store.Stored[SkillDirectoryPreferences.GlobalKey] = SkillDirectoryPreferences.Serialize(
        [new SessionFileEntry(@"C:\skills\global", true)]);
    MainViewModel shell = CreateSettingsShell(store);
    SettingsViewModel vm = CreateDialog(global: await shell.GetGlobalSkillDirectoriesAsync());
    // The row's checkbox state flips (two-way binding at runtime); the C#-native
    // stand-in for that write is the row record's copy constructor.
    vm.GlobalSkillDirectories[0] = vm.GlobalSkillDirectories[0] with { Enabled = false };
    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;
    vm.SaveCommand.Execute(null);
    Assert.NotNull(saved);

    await shell.ApplySettingsAsync(saved);

    string expected = SkillDirectoryPreferences.Serialize(
        [new SessionFileEntry(@"C:\skills\global", false)]);
    Assert.Equal(expected, store.Stored[SkillDirectoryPreferences.GlobalKey]);
    Assert.False((await shell.GetGlobalSkillDirectoriesAsync()).Single().Enabled);
  }

  [Fact]
  public async Task WorkspaceScope_UsesWorkspaceKey()
  {
    FakePreferenceStore store = new();
    MainViewModel shell = CreateSettingsShell(store);
    _ = await OpenShellAsync(shell, WsRoot);

    await shell.ApplySettingsAsync(MinimalUpdate() with
    {
      WorkspaceRoot = WsRoot,
      WorkspaceSkillDirectories = [new SessionFileEntry(@"C:\skills\ws", true)],
    });

    string expected = SkillDirectoryPreferences.Serialize(
        [new SessionFileEntry(@"C:\skills\ws", true)]);
    Assert.Equal(expected, store.Stored[SkillDirectoryPreferences.WorkspaceKey(WsRoot)]);
    Assert.False(store.Stored.ContainsKey(SkillDirectoryPreferences.GlobalKey));
    SessionFileEntry reloaded = Assert.Single(await shell.GetWorkspaceSkillDirectoriesAsync());
    Assert.Equal(@"C:\skills\ws", reloaded.Path);
  }

  [Fact]
  public async Task NullPreferences_SaveIsNoop_NoThrow()
  {
    MainViewModel shell = CreateSettingsShell(preferences: null);
    SettingsUpdate update = MinimalUpdate() with
    {
      GlobalSkillDirectories = [new SessionFileEntry(@"C:\skills\global", true)],
      WorkspaceRoot = WsRoot,
      WorkspaceSkillDirectories = [new SessionFileEntry(@"C:\skills\ws", true)],
    };

    Exception? failure = await Record.ExceptionAsync(() => shell.ApplySettingsAsync(update));

    Assert.Null(failure);
    Assert.Empty(await shell.GetGlobalSkillDirectoriesAsync());
    Assert.Empty(await shell.GetWorkspaceSkillDirectoriesAsync());
  }

  [Fact]
  public async Task DeleteAll_Deletes_The_Key()
  {
    FakePreferenceStore store = new();
    store.Stored[SkillDirectoryPreferences.GlobalKey] = SkillDirectoryPreferences.Serialize(
        [new SessionFileEntry(@"C:\skills\global", true)]);
    MainViewModel shell = CreateSettingsShell(store);

    await shell.ApplySettingsAsync(MinimalUpdate() with { GlobalSkillDirectories = [] });

    // An empty list DELETEs the preference — never an empty-array value (the
    // SessionFilePreferences contract this editor mirrors).
    Assert.Contains(SkillDirectoryPreferences.GlobalKey, store.Deletions);
    Assert.False(store.Stored.ContainsKey(SkillDirectoryPreferences.GlobalKey));
    Assert.Empty(await shell.GetGlobalSkillDirectoriesAsync());
  }

  [Fact]
  public void Save_Carries_Both_Lists_With_Their_Checkbox_State()
  {
    SettingsViewModel vm = CreateDialog(
        global: [new SessionFileEntry(@"C:\skills\global", true)],
        workspace: [new SessionFileEntry(@"C:\skills\ws", false)],
        workspaceRoot: WsRoot);

    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;
    vm.SaveCommand.Execute(null);

    Assert.NotNull(saved);
    Assert.NotNull(saved.GlobalSkillDirectories);
    Assert.NotNull(saved.WorkspaceSkillDirectories);
    Assert.True(Assert.Single(saved.GlobalSkillDirectories).Enabled);
    Assert.False(Assert.Single(saved.WorkspaceSkillDirectories).Enabled);
    Assert.Equal(WsRoot, saved.WorkspaceRoot);
  }

  // ── helpers (the EffortSettingsPersistenceTests shell shape: a faked session
  //    delegate for tab opens plus Settings + factory so ApplySettingsAsync persists) ──

  private static SettingsViewModel CreateDialog(
      IReadOnlyList<SessionFileEntry>? global = null,
      IReadOnlyList<SessionFileEntry>? workspace = null,
      string? workspaceRoot = null) => new(
      null, CommitStyle.Conventional,
      globalSkillDirectories: global, workspaceSkillDirectories: workspace,
      workspaceRoot: workspaceRoot);

  private static SettingsUpdate MinimalUpdate() =>
      new(null, CommitStyle.Conventional);

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
