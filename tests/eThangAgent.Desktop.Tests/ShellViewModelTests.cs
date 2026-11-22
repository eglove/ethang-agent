using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using eThangAgent.Transport.ACL;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>Shell-level behavior of the main window: menu bar, tab lifecycle, the
///     empty state, and the provider dimension of tabs. Session creation is faked at
///     the factory seam — the factory's own composition is covered in
///     eThangAgent.Composition.Tests.</summary>
[Trait("Category", "Process")]
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
    return new MainViewModel(create, new MainViewModelOptions { Preferences = preferences });
  }

  /// <summary>A shell bound to one workspace for its whole life, the shape the launcher
  ///     hands the main window.</summary>
  private static MainViewModel CreateBoundShell(string? workspaceRoot,
      Func<string, string, AgentSession>? factory = null, IAppPreferenceStore? preferences = null)
  {
    Task<Result<AgentSession>> create(string root, string provider) =>
        factory is null
            ? Task.FromResult(Result.Failure<AgentSession>(new DomainError("NoFactory", "no session factory configured")))
            : Task.FromResult(Result.Success(factory(root, provider)));
    return new MainViewModel(create, new MainViewModelOptions
    {
      WorkspaceRoot = workspaceRoot,
      Preferences = preferences,
    });
  }

  private static AgentSession FakeSession(string root, string provider = "openrouter",
      SessionModelPreferences? preferences = null, AgentDomain.IAgentStore? store = null)
  {
    // A session whose container is never disposed (no ServiceProvider) — tests that
    // close tabs must not touch Services. Build via a throwaway ServiceCollection so
    // DisposeAsync stays legal.
    ServiceProvider services = new ServiceCollection()
        .AddSingleton(store ?? new TestFixtures.StubStore())
        .BuildServiceProvider();
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
        Preferences: preferences);
  }

  /// <summary>A store that serves ONE root record carrying a pinned model — the record
  ///     of what actually served, which a resumed session continues with. Every other
  ///     member throws, so a restore that strays fails loudly.</summary>
  private sealed class PinnedStore(string pinnedModel) : AgentDomain.IAgentStore
  {
    public Task<Result<AgentDomain.AgentRecord>> GetAsync(AgentDomain.AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Success(AgentDomain.AgentRecord.Root(id, DateTimeOffset.UtcNow,
            @"C:\work\alpha", "openrouter", pinnedModel)));

    public Task<Result<string>> SaveAsync(AgentDomain.AgentRecord record, CancellationToken ct = default)
        => throw new NotSupportedException("SaveAsync is outside the restore path.");

    public Task<Result<string>> UpdateAsync(AgentDomain.AgentRecord record, CancellationToken ct = default)
        => throw new NotSupportedException("UpdateAsync is outside the restore path.");

    public Task<Result<string>> AppendMessageAsync(AgentDomain.AgentId id, ConversationDomain.Message message,
        CancellationToken ct = default)
        => throw new NotSupportedException("AppendMessageAsync is outside the restore path.");

    public Task<Result<string>> ReplaceTranscriptAsync(AgentDomain.AgentId id,
        IReadOnlyList<ConversationDomain.Message> messages, CancellationToken ct = default)
        => throw new NotSupportedException("ReplaceTranscriptAsync is outside the restore path.");

    public Task<Result<IReadOnlyList<ConversationDomain.Message>>> GetTranscriptAsync(AgentDomain.AgentId id,
        CancellationToken ct = default)
        => throw new NotSupportedException("GetTranscriptAsync is outside the restore path.");


    public Task<Result<IReadOnlyList<AgentDomain.AgentRecord>>> ListAllAsync(CancellationToken ct = default)
        => throw new NotSupportedException("ListAllAsync is outside the restore path.");
  }

  private sealed class FakePreferenceStore : IAppPreferenceStore
  {
    public List<(string Key, string Value)> Writes { get; } = [];

    public List<string> Deletions { get; } = [];

    /// <summary>Persisted values served back by <see cref="GetAsync"/> (empty store by
    ///     default — matching the pre-existing "remembers nothing" behavior).</summary>
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

  /// <summary>Protects keys with a recognizable marker so persistence tests can see
  ///     exactly what would land in the store (and that it is NOT plaintext).</summary>
  private sealed class FakeKeyProtector : IApiKeyProtector
  {
    public string Protect(string apiKey) => $"protected:{apiKey}";

    public string? Unprotect(string storedValue)
        => storedValue.StartsWith("protected:", StringComparison.Ordinal)
            ? storedValue["protected:".Length..]
            : null;
  }

  private static AgentSettings Settings(string? openRouter = null) => new(
      new OpenRouterSettings(openRouter, new Uri("https://openrouter.test")),
      new AgentDomain.AgentRunOptions(null, 2));

  private static MainViewModel CreateSettingsShell(
      AgentSettings settings,
      IAppPreferenceStore? preferences = null,
      IApiKeyProtector? protector = null,
      string? preferredProviderId = null)
      => new(null,
          new MainViewModelOptions
          {
            PreferredProviderId = preferredProviderId,
            Preferences = preferences,
            Settings = settings,
            SessionFactory = new AgentSessionFactory(settings),
            ApiKeyProtector = protector,
          });

  [Fact]
  public async Task AddAgentCommand_OpensAnAgentInTheBoundWorkspace()
  {
    MainViewModel vm = CreateBoundShell(@"C:\work\alpha", (root, _) => FakeSession(root));

    await vm.AddAgentCommand.ExecuteAsync(null);

    AgentTabViewModel tab = Assert.Single(vm.Tabs);
    Assert.Equal(@"C:\work\alpha", tab.WorkspaceRoot);
    Assert.Equal(tab, vm.SelectedTab);
  }

  [Fact]
  public void WorkspaceRoot_IsNormalizedFromTheOptions()
  {
    // Normalize trims the trailing separator and surrounding whitespace. It does NOT fold
    // case: case-insensitivity is WorkspaceIdentity.PathsEqual's rule, not the stored form's.
    Assert.Equal(@"C:\work\alpha", CreateBoundShell(@"C:\work\alpha\").WorkspaceRoot);
    Assert.Equal(@"c:\work\alpha", CreateBoundShell(@"  c:\work\alpha  ").WorkspaceRoot);
    Assert.Null(CreateBoundShell(null).WorkspaceRoot);
  }

  [Fact]
  public async Task OpenAgentInWorkspaceAsync_CreatesOneTabInTheBoundWorkspace_AndSelectsIt()
  {
    MainViewModel vm = CreateBoundShell(@"C:\work\alpha", (root, _) => FakeSession(root));

    Result<AgentTabViewModel> opened = await vm.OpenAgentInWorkspaceAsync();

    Assert.True(opened.IsSuccess);
    AgentTabViewModel tab = Assert.Single(vm.Tabs);
    Assert.Equal(@"C:\work\alpha", tab.Container.WorkspaceRoot);
    Assert.Equal(tab, vm.SelectedTab);
  }

  /// <summary>ONE WINDOW IS ONE GRID: a second start appends a card to the grid that exists
  ///     rather than opening another, so the window holds one grid however many agents run in
  ///     it.</summary>
  [Fact]
  public async Task OpenAgentInWorkspaceAsync_Twice_AddsACardToTheSameGrid()
  {
    MainViewModel vm = CreateBoundShell(@"C:\work\alpha", (root, _) => FakeSession(root));

    _ = await vm.OpenAgentInWorkspaceAsync();
    _ = await vm.OpenAgentInWorkspaceAsync();

    AgentTabViewModel grid = Assert.Single(vm.Tabs);
    Assert.Equal(@"C:\work\alpha", grid.WorkspaceRoot);
    Assert.Equal(grid, vm.SelectedTab);
  }

  /// <summary>The '+' control starts an agent and returns the card it occupies, appending to
  ///     the window's grid rather than opening a second one.</summary>
  [Fact]
  public async Task AddAgentInWorkspaceAsync_AppendsACard_ToTheWindowGrid()
  {
    MainViewModel vm = CreateBoundShell(@"C:\work\alpha", (root, _) => FakeSession(root));

    Result<AgentCellViewModel> first = await vm.AddAgentInWorkspaceAsync();
    Result<AgentCellViewModel> second = await vm.AddAgentInWorkspaceAsync();

    Assert.True(first.IsSuccess, first.Error?.Message);
    Assert.True(second.IsSuccess, second.Error?.Message);
    AgentTabViewModel grid = Assert.Single(vm.Tabs);
    Assert.Equal(2, grid.Cells.Count);
    Assert.NotEqual(first.Value.RootId, second.Value.RootId);
  }

  [Fact]
  public async Task OpenAgentAsync_WithTheBoundRootInAnotherSpelling_OpensNormally()
  {
    MainViewModel vm = CreateBoundShell(@"C:\work\alpha", (root, _) => FakeSession(root));

    Result<AgentTabViewModel> opened = await vm.OpenAgentAsync(@"c:\work\alpha\", "openrouter");

    Assert.True(opened.IsSuccess);
    _ = Assert.Single(vm.Tabs);
  }

  [Fact]
  public async Task OpenAgentAsync_WithAForeignRoot_FailsForeignWorkspace_AndOpensNothing()
  {
    MainViewModel vm = CreateBoundShell(@"C:\work\alpha", (root, _) => FakeSession(root));

    Result<AgentTabViewModel> opened = await vm.OpenAgentAsync(@"C:\work\beta", "openrouter");

    Assert.False(opened.IsSuccess);
    Assert.Equal("ForeignWorkspace", opened.Error.Code);
    Assert.Empty(vm.Tabs);
  }

  [Fact]
  public async Task OpenAgentInWorkspaceAsync_OnAnUnboundShell_FailsNoWorkspace_AndOpensNothing()
  {
    MainViewModel vm = CreateBoundShell(null, (root, _) => FakeSession(root));

    Result<AgentTabViewModel> opened = await vm.OpenAgentInWorkspaceAsync();

    Assert.False(opened.IsSuccess);
    Assert.Equal("NoWorkspace", opened.Error.Code);
    Assert.Empty(vm.Tabs);
  }

  [Fact]
  public void EmptyStateText_NamesTheBoundWorkspace_AndNeverPointsAtOpenWorkspace()
  {
    string bound = CreateBoundShell(@"C:\work\alpha").EmptyStateText;
    Assert.StartsWith("No agent is open.", bound, StringComparison.Ordinal);
    Assert.Contains(@"C:\work\alpha", bound, StringComparison.Ordinal);
    Assert.DoesNotContain("Open Workspace", bound, StringComparison.Ordinal);
    Assert.DoesNotContain("Open Workspace",
        CreateBoundShell(null).EmptyStateText, StringComparison.Ordinal);
  }

  [Fact]
  public async Task WorkspaceScopedLists_DefaultToTheBoundRoot()
  {
    FakePreferenceStore preferences = new();
    preferences.Stored[SessionFilePreferences.WorkspaceKey(@"C:\work\alpha")] =
        SessionFilePreferences.Serialize([new SessionFileEntry("AGENTS.md", true)]);
    preferences.Stored[SkillDirectoryPreferences.WorkspaceKey(@"C:\work\alpha")] =
        SkillDirectoryPreferences.Serialize([new SessionFileEntry(@"C:\skills", true)]);
    preferences.Stored[TrustedDirectoryPreferences.WorkspaceKey(@"C:\work\alpha")] =
        TrustedDirectoryPreferences.Serialize([new TrustedDirectoryEntry(@"D:\shared", true, false)]);
    MainViewModel vm = CreateBoundShell(@"C:\work\alpha", preferences: preferences);

    IReadOnlyList<SessionFileEntry> files = await vm.GetWorkspaceSessionFilesAsync();
    IReadOnlyList<SessionFileEntry> skills = await vm.GetWorkspaceSkillDirectoriesAsync();
    IReadOnlyList<TrustedDirectoryEntry> trusted = await vm.GetTrustedDirectoriesAsync();

    Assert.Equal("AGENTS.md", Assert.Single(files).Path);
    Assert.Equal(@"C:\skills", Assert.Single(skills).Path);
    Assert.Equal(@"D:\shared", Assert.Single(trusted).Path);
    Assert.Empty(await CreateBoundShell(null, preferences: preferences).GetWorkspaceSessionFilesAsync());
  }

  /// <summary>The window's grid is named after its workspace folder: one window is one
  ///     workspace, so the folder is the only name the grid can have.</summary>
  [Fact]
  public async Task OpenAgent_CreatesTheGrid_SelectsIt_And_Reports_HasTabs()
  {
    MainViewModel vm = CreateShell((root, _) => FakeSession(root));

    Result<AgentTabViewModel> opened = await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter");

    Assert.True(opened.IsSuccess);
    _ = Assert.Single(vm.Tabs);
    Assert.Equal(vm.Tabs[0], vm.SelectedTab);
    Assert.True(vm.HasTabs);
    Assert.Equal("alpha", opened.Value.Title);
  }

  [Fact]
  public async Task OpenAgent_BindsTabToItsProvider_ForTheStatusAndViewModel()
  {
    MainViewModel vm = CreateShell((root, provider) => FakeSession(root, provider));

    _ = await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter");

    Assert.Equal("OpenRouter", vm.Tabs[0].ViewModel.Status.Provider);
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

  /// <summary>A window is bound to ONE workspace, so a second directory is refused rather
  ///     than opened: there is no strip for it to join, and the window's grid is this
  ///     workspace's.</summary>
  [Fact]
  public async Task Opening_A_Second_Directory_IsRefused()
  {
    MainViewModel vm = CreateBoundShell(@"C:\work\alpha", (root, _) => FakeSession(root));
    _ = await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter");

    Result<AgentTabViewModel> second = await vm.OpenAgentAsync(@"C:\work\beta", "openrouter");

    Assert.False(second.IsSuccess);
    Assert.Equal("ForeignWorkspace", second.Error.Code);
    _ = Assert.Single(vm.Tabs);
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

  /// <summary>Closing a card leaves the window's grid in place: the grid is the WINDOW's, so
  ///     it outlives every agent that runs in it and the next start appends beside what is
  ///     left.</summary>
  [Fact]
  public async Task ClosingACard_KeepsTheGrid_ForTheNextAgent()
  {
    MainViewModel vm = CreateBoundShell(@"C:\work\alpha", (root, _) => FakeSession(root));
    _ = await vm.AddAgentInWorkspaceAsync();
    _ = await vm.AddAgentInWorkspaceAsync();
    AgentTabViewModel grid = Assert.Single(vm.Tabs);

    await vm.CloseCellAsync(grid.Cells[0]);
    Result<AgentCellViewModel> added = await vm.AddAgentInWorkspaceAsync();

    Assert.True(added.IsSuccess, added.Error?.Message);
    Assert.Same(grid, Assert.Single(vm.Tabs));
    Assert.Equal(2, grid.Cells.Count);
  }

  /// <summary>Closing the last card leaves the window's grid holding no agent, so the empty
  ///     pane renders and the '+' is still the way to start another.</summary>
  [Fact]
  public async Task ClosingTheLastCard_LeavesAnEmptyGrid()
  {
    MainViewModel vm = CreateBoundShell(@"C:\work\alpha", (root, _) => FakeSession(root));
    _ = await vm.AddAgentInWorkspaceAsync();
    AgentTabViewModel grid = Assert.Single(vm.Tabs);

    await vm.CloseCellAsync(Assert.Single(grid.Cells));

    Assert.Same(grid, Assert.Single(vm.Tabs));
    Assert.True(grid.IsEmpty, "the grid must report itself empty once its last card is closed");
  }

  /// <summary>A host that builds a tab directly keeps the name it always had: the workspace
  ///     folder name. The ordinal belongs to the shell, which is the only caller that opens
  ///     several tabs in one workspace.</summary>
  [Fact]
  public void ATabBuiltThroughTheExistingConstructor_ReportsTheWorkspaceFolderName()
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();

    Assert.Equal("demo", tab.Title);
  }

  /// <summary>The tab's name is an ordinal, so the tooltip is where it says which workspace,
  ///     which provider and which session it actually is.</summary>
  [Fact]
  public async Task ATabsTooltip_NamesItsWorkspace_Provider_AndSession()
  {
    MainViewModel vm = CreateBoundShell(@"C:\work\alpha", (root, provider) => FakeSession(root, provider));

    AgentTabViewModel tab = (await vm.OpenAgentInWorkspaceAsync()).Value!;

    Assert.Contains(@"C:\work\alpha", tab.Tooltip, StringComparison.Ordinal);
    Assert.Contains("openrouter", tab.Tooltip, StringComparison.Ordinal);
    Assert.Contains(tab.Container.RootId.ToString(), tab.Tooltip, StringComparison.Ordinal);
  }

  /// <summary>Every card in the window is a distinct agent: five starts leave five cards,
  ///     each with its own session, so no two cards can address the same conversation.</summary>
  [Fact]
  public async Task EveryCard_IsADistinctAgent()
  {
    MainViewModel vm = CreateBoundShell(@"C:\work\alpha", (root, _) => FakeSession(root));
    for (int i = 0; i < 5; i++)
    {
      _ = await vm.AddAgentInWorkspaceAsync();
    }

    AgentTabViewModel grid = Assert.Single(vm.Tabs);
    Assert.Equal(5, grid.Cells.Count);
    Assert.Equal(5, grid.Cells.Select(cell => cell.RootId).Distinct().Count());
  }

  [Fact]
  public async Task OpenAgent_Failure_Surfaces_Structured_Error_And_No_Tab()
  {
    MainViewModel vm = CreateShell(); // factory fails every request

    Result<AgentTabViewModel> opened = await vm.OpenAgentAsync(@"C:\work\gamma", "openrouter");

    Assert.False(opened.IsSuccess);
    Assert.Equal("NoFactory", opened.Error.Code);
    Assert.Empty(vm.Tabs);
  }

  /// <summary>Opening an agent writes NO provider preference. A session's provider comes from
  ///     the vendor each of its models names, so there is no choice left to remember - and the
  ///     retired key is neither rewritten nor deleted, which is what keeps a stored value from an
  ///     older build readable instead of silently discarded.</summary>
  [Fact]
  public async Task OpenAgent_WritesNoProviderPreference()
  {
    FakePreferenceStore preferences = new();
    MainViewModel vm = CreateShell((root, provider) => FakeSession(root, provider), preferences);

    _ = await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter");

    Assert.DoesNotContain(preferences.Writes, w => w.Key == Providers.PreferenceKey);
    Assert.DoesNotContain(preferences.Deletions, k => k == Providers.PreferenceKey);
  }

  [Fact]
  public void OpenSettingsCommand_Raises_Settings_Request()
  {
    MainViewModel vm = CreateShell();
    bool raised = false;
    vm.SettingsRequested += (_, _) => raised = true;

    vm.OpenSettingsCommand.Execute(null);

    Assert.True(raised);
  }

  [Fact]
  public void Ctor_Without_Delegate_Or_Settings_Fails_Fast()
      => _ = Assert.Throws<ArgumentException>(() => new MainViewModel(null));

  [Fact]
  public async Task ApplySettings_Persists_Protected_Keys_And_Refreshes_Providers()
  {
    FakePreferenceStore preferences = new();
    MainViewModel vm = CreateSettingsShell(Settings(), preferences, new FakeKeyProtector());
    Assert.False(vm.HasConfiguredProvider);

    await vm.ApplySettingsAsync(new SettingsUpdate("  sk-or-v1-abc  ", CommitStyle.Conventional));

    // Keys land trimmed and PROTECTED — never plaintext. The Agents/Advanced knobs
    // persist in the same pass (all defaults here).
    Assert.Equal(
        [
            (OpenRouterSettings.PreferenceKey, "protected:sk-or-v1-abc"),
            (AppPreferenceCommitStyleProvider.PreferenceKey, "Conventional"),
            (AgentPreferenceKeys.ComputerUseEnabled, "false"),
        ],
        preferences.Writes);
    // The blank knob texts clear exactly their own preferences (blank = absent,
    // never an empty-string value): the Agents/Advanced fields this legacy-shaped
    // update leaves unset all delete.
    Assert.Equal(
        [OpenRouterSettings.ManagementKeyPreferenceKey,
         CloudflareSettings.AccountIdPreferenceKey, CloudflareSettings.ApiTokenPreferenceKey,
         CloudflareSettings.GatewayIdPreferenceKey,
         AgentPreferenceKeys.MaxConcurrentAgents, AgentPreferenceKeys.DefaultModel,
         AgentPreferenceKeys.WatchdogTickInterval, AgentPreferenceKeys.WatchdogIdleThreshold,
         AgentPreferenceKeys.WatchdogMaxWrapUpAttempts,
         AgentPreferenceKeys.RootTurnIdle,
         AgentPreferenceKeys.OpenRouterBaseUrl, SessionHandoffPreferences.Key,
         RetentionSweep.WindowHoursKey, PathToolPreferences.Key,
         SessionFilePreferences.GlobalKey, SkillDirectoryPreferences.GlobalKey],
        preferences.Deletions);

    Assert.Equal(["openrouter"], vm.AvailableProviders.Select(p => p.Id));
    Assert.True(vm.HasConfiguredProvider);
    Assert.Equal("sk-or-v1-abc", vm.ConfiguredOpenRouterKey);
  }

  [Fact]
  public async Task ApplySettings_Cleared_Keys_Delete_Preferences_And_Drop_Providers()
  {
    FakePreferenceStore preferences = new();
    MainViewModel vm = CreateSettingsShell(
        Settings(openRouter: "sk-or-v1-abc"), preferences, new FakeKeyProtector(),
        preferredProviderId: Providers.OpenRouter);
    Assert.Equal(["openrouter"], vm.AvailableProviders.Select(p => p.Id));

    await vm.ApplySettingsAsync(new SettingsUpdate(null, CommitStyle.Conventional));

    Assert.Equal(
        [OpenRouterSettings.PreferenceKey, OpenRouterSettings.ManagementKeyPreferenceKey,
         CloudflareSettings.AccountIdPreferenceKey, CloudflareSettings.ApiTokenPreferenceKey,
         CloudflareSettings.GatewayIdPreferenceKey,
         AgentPreferenceKeys.MaxConcurrentAgents, AgentPreferenceKeys.DefaultModel,
         AgentPreferenceKeys.WatchdogTickInterval, AgentPreferenceKeys.WatchdogIdleThreshold,
         AgentPreferenceKeys.WatchdogMaxWrapUpAttempts,
         AgentPreferenceKeys.RootTurnIdle,
         AgentPreferenceKeys.OpenRouterBaseUrl, SessionHandoffPreferences.Key,
         RetentionSweep.WindowHoursKey, PathToolPreferences.Key,
         SessionFilePreferences.GlobalKey, SkillDirectoryPreferences.GlobalKey],
        preferences.Deletions);
    Assert.Equal(
        [
            (AppPreferenceCommitStyleProvider.PreferenceKey, "Conventional"),
            (AgentPreferenceKeys.ComputerUseEnabled, "false"),
        ],
        preferences.Writes);
    Assert.Empty(vm.AvailableProviders);
    Assert.False(vm.HasConfiguredProvider);
    // Nothing configured to preselect — the stored preference survives untouched.
    Assert.Equal(Providers.OpenRouter, vm.PreferredProviderId);
  }

  [Fact]
  public async Task ApplySettings_Without_Protector_Never_Persists_Plaintext()
  {
    FakePreferenceStore preferences = new();
    MainViewModel vm = CreateSettingsShell(Settings(), preferences, protector: null);

    await vm.ApplySettingsAsync(new SettingsUpdate("sk-or-v1-abc", CommitStyle.Conventional));

    // Strict boundary: no protector means no durable key, ever. The in-memory
    // surface still reflects the edit — only persistence is skipped. The commit
    // style persists plaintext: it is not a secret.
    Assert.Equal(
        [
            (AppPreferenceCommitStyleProvider.PreferenceKey, "Conventional"),
            (AgentPreferenceKeys.ComputerUseEnabled, "false"),
        ],
        preferences.Writes);
    Assert.Equal(
        [OpenRouterSettings.ManagementKeyPreferenceKey,
         CloudflareSettings.AccountIdPreferenceKey, CloudflareSettings.ApiTokenPreferenceKey,
         CloudflareSettings.GatewayIdPreferenceKey,
         AgentPreferenceKeys.MaxConcurrentAgents, AgentPreferenceKeys.DefaultModel,
         AgentPreferenceKeys.WatchdogTickInterval, AgentPreferenceKeys.WatchdogIdleThreshold,
         AgentPreferenceKeys.WatchdogMaxWrapUpAttempts,
         AgentPreferenceKeys.RootTurnIdle,
         AgentPreferenceKeys.OpenRouterBaseUrl, SessionHandoffPreferences.Key,
         RetentionSweep.WindowHoursKey, PathToolPreferences.Key,
         SessionFilePreferences.GlobalKey, SkillDirectoryPreferences.GlobalKey],
        preferences.Deletions);
    Assert.Equal("sk-or-v1-abc", vm.ConfiguredOpenRouterKey);
  }

  [Fact]
  public void OpenAgent_Is_Gated_When_No_Provider_Is_Configured()
  {
    MainViewModel vm = new(
        (_, _) => Task.FromResult(Result.Success(FakeSession(@"C:\work"))),
        new MainViewModelOptions { AvailableProviders = [] });

    Assert.False(vm.HasConfiguredProvider);
    Assert.False(vm.AddAgentCommand.CanExecute(null));
  }

  // ── Model choice (item 117: the picker folded into Model Settings; the shell
  //    applies a saved snapshot's model choice through the same paths) ────────

  [Fact]
  public async Task ApplyModelChoice_Updates_Session_And_PersistsTheChoice_Per_Workspace()
  {
    // THE CHOICE PERSISTS TOGETHER: the model and the routing variant it was chosen for. A model
    // without its variant is half a decision - the model name says nothing about whether the
    // turn should route for throughput or for price.
    FakePreferenceStore preferences = new();
    MainViewModel vm = CreateShell((root, provider) => FakeSession(
        root, provider, new SessionModelPreferences()), preferences);
    _ = await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter");

    await vm.ApplyModelChoiceAsync(["anthropic/claude"], ModelVariant.Nitro);

    AgentSessionViewModel tab = vm.SelectedTab!.ViewModel;
    Assert.Equal("anthropic/claude", tab.Status.ModelId);
    Assert.Equal(ModelVariants.Describe(ModelVariant.Nitro), tab.Status.ServingVariant);
    Assert.Contains(tab.Transcript.Entries.OfType<NoticeEntry>(),
        n => n.Text.Contains("anthropic/claude", StringComparison.Ordinal));
    (string key, string value) = Assert.Single(preferences.Writes,
        w => w.Key.StartsWith("model_provider_choice:", StringComparison.Ordinal));
    Assert.Equal($"model_provider_choice:openrouter:{Path.GetFullPath(@"C:\work\alpha")}", key);
    ModelSelection stored = ModelSelection.Parse(value).Value!;
    Assert.Equal(["anthropic/claude"], stored.ModelIds);
    Assert.Equal(ModelVariant.Nitro, stored.Variant);
  }

  [Fact]
  public async Task ApplyModelChoice_WithoutAVariant_LeavesRoutingsDefaultInPlace()
  {
    // A model committed without a variant keeps the provider's own routing, and the status line
    // shows NOTHING rather than naming a Standard nobody chose.
    FakePreferenceStore preferences = new();
    MainViewModel vm = CreateShell((root, provider) => FakeSession(
        root, provider, new SessionModelPreferences()), preferences);
    _ = await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter");

    await vm.ApplyModelChoiceAsync(["anthropic/claude"]);

    Assert.Equal(string.Empty, vm.SelectedTab!.ViewModel.Status.ServingVariant);
    (string _, string value) = Assert.Single(preferences.Writes,
        w => w.Key.StartsWith("model_provider_choice:", StringComparison.Ordinal));
    Assert.Equal(ModelVariant.Standard, ModelSelection.Parse(value).Value!.Variant);
  }

  [Fact]
  public async Task OpeningAWorkspace_RestoresThePersistedChoice()
  {
    // A committed choice restores on the next open - the same model under the same tradeoff.
    FakePreferenceStore preferences = new();
    string root = Path.GetFullPath(@"C:\work\alpha");
    preferences.Stored[$"model_provider_choice:openrouter:{root}"] =
        ModelSelection.Create(["anthropic/claude"], ModelVariant.Floor).Value!.Serialize();
    SessionModelPreferences live = new();
    MainViewModel vm = CreateShell((r, provider) => FakeSession(r, provider, live), preferences);

    _ = await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter");

    Assert.Equal(["anthropic/claude"], live.ModelIds);
    Assert.Equal(ModelVariant.Floor, live.ModelVariant);
    Assert.Equal(ModelVariants.Describe(ModelVariant.Floor), vm.SelectedTab!.ViewModel.Status.ServingVariant);
  }

  [Theory]
  // The two shapes a stored choice can have from before the variant existed. BOTH restore the
  // model and leave the variant UNSET: the retired provider pin cannot be resurrected as a
  // routing intent the user never picked, and refusing the value outright would drop a model
  // they did choose.
  [InlineData("model_choice")]
  [InlineData("model_provider_choice")]
  public async Task ALegacyStoredChoice_RestoresTheModel_AndLeavesTheVariantUnset(string keyKind)
  {
    FakePreferenceStore preferences = new();
    string root = Path.GetFullPath(@"C:\work\alpha");
    preferences.Stored[$"{keyKind}:openrouter:{root}"] = keyKind == "model_choice"
        ? "anthropic/claude"
        : /*lang=json,strict*/ """{"ModelId":"anthropic/claude","ProviderName":"DeepInfra"}""";
    SessionModelPreferences live = new();
    MainViewModel vm = CreateShell((r, provider) => FakeSession(r, provider, live), preferences);

    _ = await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter");

    Assert.Equal(["anthropic/claude"], live.ModelIds);
    Assert.Null(live.ModelVariant);
    Assert.Equal(string.Empty, vm.SelectedTab!.ViewModel.Status.ServingVariant);
  }

  [Fact]
  public async Task ApplyModelChoice_WithASingleModel_PersistsItAndNamesItOnTheBar()
  {
    // A one-entry list is what every workspace saved before the ordered list existed, so it has to
    // round-trip through the same path a longer one does.
    FakePreferenceStore preferences = new();
    MainViewModel vm = CreateShell((root, provider) => FakeSession(
        root, provider, new SessionModelPreferences { ModelIds = ["anthropic/claude"] }), preferences);
    _ = await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter");

    await vm.ApplyModelChoiceAsync(["test/model"]);

    AgentTabViewModel tab = vm.SelectedTab!;
    Assert.Equal(["test/model"], tab.Container.Preferences!.ModelIds);
    Assert.Equal("test/model", tab.ViewModel.Status.ModelId);
    Assert.Equal(string.Empty, tab.ViewModel.Status.ServingVariant); // no variant committed
    Assert.Equal(string.Empty, tab.ViewModel.Status.BackupCountText); // no failover to report
    (string key, string value) = Assert.Single(preferences.Writes,
        w => w.Key.StartsWith("model_provider_choice:", StringComparison.Ordinal));
    Assert.Equal($"model_provider_choice:openrouter:{Path.GetFullPath(@"C:\work\alpha")}", key);
    Assert.Equal(["test/model"], ModelSelection.Parse(value).Value!.ModelIds);
  }

  [Fact]
  public async Task ApplyModelChoice_WithAnOrderedList_PersistsTheOrderAndCountsTheBackups()
  {
    // THE ORDER IS THE DECISION: the stored value carries the list as entered, entry 1 first, and the
    // status bar names the head with the number of backups beside it.
    FakePreferenceStore preferences = new();
    MainViewModel vm = CreateShell((root, provider) => FakeSession(
        root, provider, new SessionModelPreferences()), preferences);
    _ = await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter");

    await vm.ApplyModelChoiceAsync(["alpha/model", "beta/other", "gamma/third"], ModelVariant.Nitro);

    AgentTabViewModel tab = vm.SelectedTab!;
    Assert.Equal(["alpha/model", "beta/other", "gamma/third"], tab.Container.Preferences!.ModelIds);
    Assert.Equal("alpha/model", tab.ViewModel.Status.ModelId); // entry 1 runs
    Assert.Equal("· 2 backups", tab.ViewModel.Status.BackupCountText);
    (string _, string value) = Assert.Single(preferences.Writes,
        w => w.Key.StartsWith("model_provider_choice:", StringComparison.Ordinal));
    ModelSelection stored = ModelSelection.Parse(value).Value!;
    Assert.Equal(["alpha/model", "beta/other", "gamma/third"], stored.ModelIds);
    Assert.Equal(ModelVariant.Nitro, stored.Variant);
  }

  [Fact]
  public async Task ApplyModelChoice_Without_Tab_Is_A_NoOp()
  {
    MainViewModel vm = CreateShell();

    await vm.ApplyModelChoiceAsync(["anthropic/claude"]);

    Assert.Null(vm.SelectedTab); // nothing to reach — must not throw
  }

  [Fact]
  public async Task OpenAgent_Restores_Persisted_Model_Choice_Into_The_Session()
  {
    FakePreferenceStore preferences = new();
    string root = Path.GetFullPath(@"C:\work\alpha");
    preferences.Stored[$"model_choice:openrouter:{root}"] = "anthropic/claude";
    MainViewModel vm = CreateShell((r, provider) => FakeSession(
        r, provider, new SessionModelPreferences()), preferences);

    _ = await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter");

    AgentTabViewModel tab = vm.SelectedTab!;
    Assert.Equal(["anthropic/claude"], tab.Container.Preferences!.ModelIds);
    Assert.Equal("anthropic/claude", tab.ViewModel.Status.ModelId);
    // The restore is silent — no notice before any turn has run.
    Assert.DoesNotContain(tab.ViewModel.Transcript.Entries, e => e is NoticeEntry);
  }

  [Fact]
  public async Task OpenAgent_Names_The_Pinned_Model_As_The_Serving_Model()
  {
    // The pin is what ACTUALLY served, so a resumed session continues with it - and the
    // live preference must name it too, or the turn would run the session default while
    // the status bar showed the pin (2026-10-22: the display must never claim a model
    // the turn does not run on).
    FakePreferenceStore preferences = new();
    SessionModelPreferences live = new();
    MainViewModel vm = CreateShell((root, provider) =>
        FakeSession(root, provider, live, new PinnedStore("anthropic/claude")), preferences);

    _ = await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter");

    AgentTabViewModel tab = vm.SelectedTab!;
    Assert.Equal(["anthropic/claude"], live.ModelIds);
    Assert.Equal("anthropic/claude", tab.ViewModel.Status.ModelId);
  }

  [Fact]
  public async Task OpenAgent_Names_The_Session_Default_When_None_Persisted()
  {
    // Nothing was persisted, so the session states its default model: the resolver
    // never substitutes one, and the status bar must name what will serve.
    FakePreferenceStore preferences = new();
    MainViewModel vm = CreateShell((root, provider) => FakeSession(
        root, provider, new SessionModelPreferences()), preferences);

    _ = await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter");

    AgentTabViewModel tab = vm.SelectedTab!;
    Assert.Equal(["test/model"], tab.Container.Preferences!.ModelIds);
    Assert.Equal("test/model", tab.ViewModel.Status.ModelId);
  }

  [Fact]
  public async Task ASiblingLaunchedFromAnOrderedList_InheritsTheWholeListInOrder()
  {
    // Behaviour 10: a sibling inherits the workspace's ordered list, not the head it was launched
    // with. The launch names no model, so the ordinary inheritance applies - and that inheritance
    // is the restore path, which lands every entry in the stored order. A launch that carried only
    // one model would leave the sibling with no failover while the session it started from had
    // one, and the two would send different `models` arrays from the same workspace.
    FakePreferenceStore preferences = new();
    string root = Path.GetFullPath(@"C:\work\alpha");
    preferences.Stored[$"model_provider_choice:openrouter:{root}"] = ModelSelection.Create(
        ["alpha/model", "beta/other"], ModelVariant.Nitro).Value!.Serialize();
    SessionModelPreferences live = new();
    MainViewModel vm = CreateSiblingShell(preferences, live);
    AgentTabViewModel tab = (await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter").ConfigureAwait(true)).Value!;
    Assert.Equal(["alpha/model", "beta/other"], live.ModelIds);

    await vm.HandleSessionRequestAsync(tab,
        new SessionRequestPayload("req-sibling", "audit the retry path", HandoffSummary: null)
        {
          SiblingOfWorkspaceId = tab.WorkspaceId,
          Label = "auditor",
        }).ConfigureAwait(true);

    // The sibling's cell carries the same ordered list, under the same one variant.
    AgentCellViewModel sibling = tab.Cells[^1];
    Assert.NotEqual(tab.Cells[0].RootId, sibling.RootId);
    Assert.Equal(["alpha/model", "beta/other"], sibling.Container!.Preferences!.ModelIds);
    Assert.Equal(ModelVariant.Nitro, sibling.Container.Preferences.ModelVariant);
    // Its own bar names the head and counts the backup, so nothing about the launch degraded it.
    Assert.Equal("alpha/model", sibling.ViewModel.Status.ModelId);
    Assert.Equal("· 1 backup", sibling.ViewModel.Status.BackupCountText);
  }

  [Fact]
  public async Task ASiblingLaunchedFromAnOrderedList_CarriesTheSameModelsArray()
  {
    // The sibling's turn is assembled from the inherited preference, so the request it sends
    // states the whole list in order - the same array the launching session sends.
    FakePreferenceStore preferences = new();
    string root = Path.GetFullPath(@"C:\work\alpha");
    preferences.Stored[$"model_provider_choice:openrouter:{root}"] = ModelSelection.Create(
        ["alpha/model", "beta/other"], ModelVariant.Nitro).Value!.Serialize();
    SessionModelPreferences live = new();
    MainViewModel vm = CreateSiblingShell(preferences, live);
    AgentTabViewModel tab = (await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter").ConfigureAwait(true)).Value!;

    await vm.HandleSessionRequestAsync(tab,
        new SessionRequestPayload("req-sibling", "audit the retry path", HandoffSummary: null)
        {
          SiblingOfWorkspaceId = tab.WorkspaceId,
        }).ConfigureAwait(true);

    // The carrier the sibling will hand its host, read through the production codec, is the whole
    // ordered list plus the variant the launching session walks it under.
    SessionModelPreferences siblingPreferences = tab.Cells[^1].Container!.Preferences!;
    SessionModelPreferences carried =
        SessionModelPreferencesJson.Deserialize(SessionModelPreferencesJson.Serialize(siblingPreferences)).Value!;
    Assert.Equal(["alpha/model", "beta/other"], carried.ModelIds);
    Assert.Equal(ModelVariant.Nitro, carried.ModelVariant);
  }

  /// <summary>A shell whose sessions carry a host channel and a workspace identity, which are
  ///     what the sibling path needs: a launch is ANSWERED on the asking session's channel, and it
  ///     is placed by the workspace the request names.</summary>
  private static MainViewModel CreateSiblingShell(IAppPreferenceStore preferences,
      SessionModelPreferences modelPreferences)
      => new((root, provider) =>
      {
        ServiceProvider services = new ServiceCollection()
            .AddSingleton<AgentDomain.IAgentStore>(new TestFixtures.StubStore())
            .AddSingleton<StateDomain.IWorkspaceContext>(new FixedWorkspaceContext("ws-alpha", root))
            .AddSingleton<IRootTurnChannel>(new FakeRootTurnChannel([], holdOpen: false))
            .BuildServiceProvider();
        return Task.FromResult(Result.Success(new AgentSession(
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
            Preferences: modelPreferences)));
      }, new MainViewModelOptions { Preferences = preferences });

  // ── Effort choice (item 117: the picker folded into Model Settings) ──────

  [Fact]
  public async Task ApplyEffortChoice_Updates_Session_And_Persists_Per_Workspace()
  {
    FakePreferenceStore preferences = new();
    MainViewModel vm = CreateShell((root, provider) => FakeSession(
        root, provider, new SessionModelPreferences()), preferences);
    _ = await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter");

    await vm.ApplyEffortChoiceAsync(ReasoningEffort.ExtraHigh);

    AgentTabViewModel tab = vm.SelectedTab!;
    Assert.Equal(ReasoningEffort.ExtraHigh, tab.Container.Preferences!.ReasoningEffort);
    Assert.Contains(tab.ViewModel.Transcript.Entries.OfType<NoticeEntry>(),
        n => n.Text.Contains("Extra High", StringComparison.Ordinal));
    (string key, string value) = Assert.Single(preferences.Writes,
        w => w.Key.StartsWith("effort_choice:", StringComparison.Ordinal));
    Assert.Equal($"effort_choice:openrouter:{Path.GetFullPath(@"C:\work\alpha")}", key);
    Assert.Equal(nameof(ReasoningEffort.ExtraHigh), value);
  }

  [Fact]
  public async Task ApplyEffortChoice_Default_Clears_Session_And_Deletes_Persisted_Choice()
  {
    FakePreferenceStore preferences = new();
    MainViewModel vm = CreateShell((root, provider) => FakeSession(
        root, provider, new SessionModelPreferences { ReasoningEffort = ReasoningEffort.High }), preferences);
    _ = await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter");

    await vm.ApplyEffortChoiceAsync(null);

    AgentTabViewModel tab = vm.SelectedTab!;
    Assert.Null(tab.Container.Preferences!.ReasoningEffort);
    string expectedKey = $"effort_choice:openrouter:{Path.GetFullPath(@"C:\work\alpha")}";
    Assert.Equal([expectedKey], preferences.Deletions);
    // The open persisted the provider preference; the default choice added NO effort write.
    Assert.DoesNotContain(preferences.Writes,
        w => w.Key.StartsWith("effort_choice:", StringComparison.Ordinal));
  }

  [Fact]
  public async Task ApplyEffortChoice_Without_Tab_Is_A_NoOp()
  {
    MainViewModel vm = CreateShell();

    await vm.ApplyEffortChoiceAsync(ReasoningEffort.High);

    Assert.Null(vm.SelectedTab); // nothing to reach — must not throw
  }

  [Fact]
  public async Task OpenAgent_Restores_Persisted_Effort_Choice_Into_The_Session()
  {
    FakePreferenceStore preferences = new();
    string root = Path.GetFullPath(@"C:\work\alpha");
    preferences.Stored[$"effort_choice:openrouter:{root}"] = "ExtraHigh";
    MainViewModel vm = CreateShell((r, provider) => FakeSession(
        r, provider, new SessionModelPreferences()), preferences);

    _ = await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter");

    AgentTabViewModel tab = vm.SelectedTab!;
    Assert.Equal(ReasoningEffort.ExtraHigh, tab.Container.Preferences!.ReasoningEffort);
    Assert.Equal("Extra High", tab.ViewModel.Status.Effort);
    // The restore is silent — no notice before any turn has run.
    Assert.DoesNotContain(tab.ViewModel.Transcript.Entries, e => e is NoticeEntry);
  }

  [Fact]
  public async Task OpenAgent_Ignores_A_Corrupt_Persisted_Effort_Value()
  {
    FakePreferenceStore preferences = new();
    string root = Path.GetFullPath(@"C:\work\alpha");
    preferences.Stored[$"effort_choice:openrouter:{root}"] = "ultra";
    MainViewModel vm = CreateShell((r, provider) => FakeSession(
        r, provider, new SessionModelPreferences()), preferences);

    _ = await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter");

    Assert.Null(vm.SelectedTab!.Container.Preferences!.ReasoningEffort);
  }

  // ── Tab-scoped rail buttons: MCP Servers is guarded on HasSelectedTab, so its
  //    CanExecute must re-query when the selection changes — a button Avalonia
  //    never re-evaluates stays greyed forever.

  [Fact]
  public void McpServers_Command_Is_Gated_When_No_Tab_Is_Selected()
  {
    MainViewModel vm = CreateShell();

    Assert.False(vm.OpenMcpServersCommand.CanExecute(null));
  }

  [Fact]
  public async Task McpServers_Command_Enables_Once_A_Tab_Is_Selected()
  {
    MainViewModel vm = CreateShell((root, _) => FakeSession(root));

    _ = await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter");

    Assert.True(vm.OpenMcpServersCommand.CanExecute(null));
  }

  [Fact]
  public async Task McpServers_Command_Disables_Again_When_The_Last_Tab_Closes()
  {
    MainViewModel vm = CreateShell((root, _) => FakeSession(root));
    AgentTabViewModel alpha = (await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter")).Value!;

    await vm.CloseTabAsync(alpha);

    Assert.False(vm.OpenMcpServersCommand.CanExecute(null));
  }

  [Fact]
  public async Task McpServers_Command_Raises_Its_Dialog_Request()
  {
    MainViewModel vm = CreateShell((root, _) => FakeSession(root));
    _ = await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter");
    bool mcpRaised = false;
    vm.McpServersRequested += (_, _) => mcpRaised = true;

    vm.OpenMcpServersCommand.Execute(null);

    Assert.True(mcpRaised);
  }

  [Fact]
  public async Task ApplySettings_HandoffOff_DeletesThePreference_AndPrefillsOff()
  {
    // Off is the shipped default, so it is stored as ABSENT (a delete), never as the
    // string "false" — the same "blank means absent" rule every other knob follows.
    FakePreferenceStore preferences = new();
    MainViewModel vm = CreateSettingsShell(Settings(), preferences, new FakeKeyProtector());
    Assert.False(vm.ConfiguredCompactionHandoff);

    await vm.ApplySettingsAsync(new SettingsUpdate(null, CommitStyle.Conventional,
        CompactionHandoff: false));

    Assert.Contains(SessionHandoffPreferences.Key, preferences.Deletions);
    Assert.DoesNotContain(preferences.Writes, w => w.Key == SessionHandoffPreferences.Key);
    Assert.False(vm.ConfiguredCompactionHandoff);
  }

  [Fact]
  public async Task ApplySettings_HandoffOn_PersistsTrue_AndPrefillsOn()
  {
    FakePreferenceStore preferences = new();
    MainViewModel vm = CreateSettingsShell(Settings(), preferences, new FakeKeyProtector());

    await vm.ApplySettingsAsync(new SettingsUpdate(null, CommitStyle.Conventional,
        CompactionHandoff: true));

    // The exact lowercase string the strict parser binds — never a coerced form.
    Assert.Contains((SessionHandoffPreferences.Key, "true"), preferences.Writes);
    Assert.DoesNotContain(SessionHandoffPreferences.Key, preferences.Deletions);
    Assert.True(vm.ConfiguredCompactionHandoff);
    // The round trip closes: what was written parses back to the same choice.
    Assert.True(SessionHandoffPreferences.TryParse(
        preferences.Stored[SessionHandoffPreferences.Key]).Value);
  }

  [Fact]
  public void ConfiguredCompactionHandoff_PrefillsFromTheHostLoadedValue()
  {
    // The host loads the stored preference at startup (DesktopHost) and passes it in; the
    // modal's checkbox must show what is actually stored, not a hardcoded default.
    MainViewModel vm = new(null, new MainViewModelOptions
    {
      Settings = Settings(),
      SessionFactory = new AgentSessionFactory(Settings()),
      CompactionHandoff = true,
    });

    Assert.True(vm.ConfiguredCompactionHandoff);
  }

  [Fact]
  public async Task ApplySettings_Persists_And_Applies_The_Commit_Style()
  {
    FakePreferenceStore preferences = new();
    MainViewModel vm = CreateSettingsShell(Settings(), preferences, new FakeKeyProtector());

    await vm.ApplySettingsAsync(new SettingsUpdate(null, CommitStyle.Gitmoji));

    Assert.Equal(
        [
            (AppPreferenceCommitStyleProvider.PreferenceKey, "Gitmoji"),
            (AgentPreferenceKeys.ComputerUseEnabled, "false"),
        ],
        preferences.Writes);
  }
}
