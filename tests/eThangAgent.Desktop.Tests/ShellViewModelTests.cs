using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ToolDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.Zai.ACL;
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
    return new MainViewModel(create, new MainViewModelOptions { Preferences = preferences });
  }

  private static AgentSession FakeSession(string root, string provider = "openrouter",
      SessionModelPreferences? preferences = null)
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
        Model: ModelConfig.Create("test/model", null, 128, 0.1f, 8192).Value!,
        WorkspaceRoot: root,
        ProviderName: provider,
        ClarifyChannel: null!,
        Inbox: new AgentDomain.AgentInbox(),
        ChildRuntime: new TestFixtures.StubAgentRuntime(),
        Preferences: preferences);
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

  private static AgentSettings Settings(string? openRouter = null, string? zai = null) => new(
      new OpenRouterSettings(openRouter, new Uri("https://openrouter.test")),
      new ZaiSettings(zai, new Uri("https://zai.test")),
      new AgentDomain.SubAgentOptions(null, TimeSpan.FromSeconds(300), 2));

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
    Assert.Equal("alpha", opened.Value.Title);
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
    Assert.Equal("NoFactory", opened.Error.Code);
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

    await vm.ApplySettingsAsync(new SettingsUpdate("  sk-or-v1-abc  ", " zai-key ",
        ZaiEndpointMode.CodingPlan, CommitStyle.Conventional));

    // Keys land trimmed and PROTECTED — never plaintext; the mode lands plaintext
    // (it is not a secret); nothing to delete.
    Assert.Equal(
        [
            (OpenRouterSettings.PreferenceKey, "protected:sk-or-v1-abc"),
            (ZaiSettings.PreferenceKey, "protected:zai-key"),
            (ZaiSettings.EndpointModePreferenceKey, "coding"),
            (AppPreferenceCommitStyleProvider.PreferenceKey, "Conventional"),
        ],
        preferences.Writes);
    Assert.Empty(preferences.Deletions);

    Assert.Equal(["openrouter", "zai"], vm.AvailableProviders.Select(p => p.Id));
    Assert.True(vm.HasConfiguredProvider);
    Assert.Equal("sk-or-v1-abc", vm.ConfiguredOpenRouterKey);
    Assert.Equal("zai-key", vm.ConfiguredZaiKey);
    Assert.Equal(ZaiEndpointMode.CodingPlan, vm.ConfiguredZaiEndpointMode);
  }

  [Fact]
  public async Task ApplySettings_Cleared_Keys_Delete_Preferences_And_Drop_Providers()
  {
    FakePreferenceStore preferences = new();
    MainViewModel vm = CreateSettingsShell(
        Settings(openRouter: "sk-or-v1-abc"), preferences, new FakeKeyProtector(),
        preferredProviderId: Providers.OpenRouter);
    Assert.Equal(["openrouter"], vm.AvailableProviders.Select(p => p.Id));

    await vm.ApplySettingsAsync(new SettingsUpdate(null, null, ZaiEndpointMode.CodingPlan, CommitStyle.Conventional));

    Assert.Equal([OpenRouterSettings.PreferenceKey, ZaiSettings.PreferenceKey], preferences.Deletions);
    Assert.Equal(
        [
            (ZaiSettings.EndpointModePreferenceKey, "coding"),
            (AppPreferenceCommitStyleProvider.PreferenceKey, "Conventional"),
        ],
        preferences.Writes);
    Assert.Empty(vm.AvailableProviders);
    Assert.False(vm.HasConfiguredProvider);
    // Nothing configured to preselect — the stored preference survives untouched.
    Assert.Equal(Providers.OpenRouter, vm.PreferredProviderId);
  }

  [Fact]
  public async Task ApplySettings_Revalidates_Preferred_Provider()
  {
    MainViewModel vm = CreateSettingsShell(
        Settings(openRouter: "sk-or-v1-abc", zai: "zai-key"),
        new FakePreferenceStore(), new FakeKeyProtector(),
        preferredProviderId: Providers.Zai);
    Assert.Equal("zai", vm.PreferredProviderId);

    await vm.ApplySettingsAsync(new SettingsUpdate("sk-or-v1-abc", "", ZaiEndpointMode.CodingPlan, CommitStyle.Conventional)); // z.ai key cleared

    Assert.Equal(["openrouter"], vm.AvailableProviders.Select(p => p.Id));
    Assert.Equal("openrouter", vm.PreferredProviderId);
  }

  [Fact]
  public async Task ApplySettings_Without_Protector_Never_Persists_Plaintext()
  {
    FakePreferenceStore preferences = new();
    MainViewModel vm = CreateSettingsShell(Settings(), preferences, protector: null);

    await vm.ApplySettingsAsync(new SettingsUpdate("sk-or-v1-abc", null, ZaiEndpointMode.CodingPlan, CommitStyle.Conventional));

    // Strict boundary: no protector means no durable key, ever. The in-memory
    // surface still reflects the edit — only persistence is skipped. (The z.ai
    // delete no-ops — the key was never stored.) The plaintext mode preference
    // still lands: it is not a secret and needs no protector.
    Assert.Equal(
        [
            (ZaiSettings.EndpointModePreferenceKey, "coding"),
            (AppPreferenceCommitStyleProvider.PreferenceKey, "Conventional"),
        ],
        preferences.Writes);
    Assert.Equal([ZaiSettings.PreferenceKey], preferences.Deletions);
    Assert.Equal("sk-or-v1-abc", vm.ConfiguredOpenRouterKey);
  }

  [Fact]
  public async Task ApplySettings_Persists_And_Applies_Zai_Endpoint_Mode()
  {
    FakePreferenceStore preferences = new();
    MainViewModel vm = CreateSettingsShell(Settings(zai: "zai-key"), preferences, new FakeKeyProtector());

    await vm.ApplySettingsAsync(new SettingsUpdate(null, "zai-key", ZaiEndpointMode.GeneralApi, CommitStyle.Conventional));

    Assert.Equal(
        [
            (ZaiSettings.PreferenceKey, "protected:zai-key"),
            (ZaiSettings.EndpointModePreferenceKey, "general"),
            (AppPreferenceCommitStyleProvider.PreferenceKey, "Conventional"),
        ],
        preferences.Writes);
    Assert.Equal(ZaiEndpointMode.GeneralApi, vm.ConfiguredZaiEndpointMode);
  }

  [Fact]
  public void OpenAgent_Is_Gated_When_No_Provider_Is_Configured()
  {
    MainViewModel vm = new(
        (_, _) => Task.FromResult(Result.Success(FakeSession(@"C:\work"))),
        new MainViewModelOptions { AvailableProviders = [] });

    Assert.False(vm.HasConfiguredProvider);
    Assert.False(vm.OpenAgentCommand.CanExecute(null));
  }

  // ── Model picker ──────────────────────────────────────────────────────────

  [Fact]
  public async Task ChooseModelCommand_Gated_On_Selected_Tab_And_Raises_Request()
  {
    MainViewModel vm = CreateShell((root, provider) => FakeSession(
        root, provider, new SessionModelPreferences()));
    bool raised = false;
    vm.ModelPickerRequested += (_, _) => raised = true;

    Assert.False(vm.HasSelectedTab);
    Assert.False(vm.ChooseModelCommand.CanExecute(null));

    _ = await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter");

    Assert.True(vm.HasSelectedTab);
    Assert.True(vm.ChooseModelCommand.CanExecute(null));
    vm.ChooseModelCommand.Execute(null);
    Assert.True(raised);
  }

  [Fact]
  public async Task ApplyModelChoice_Updates_Session_And_Persists_Per_Workspace()
  {
    FakePreferenceStore preferences = new();
    MainViewModel vm = CreateShell((root, provider) => FakeSession(
        root, provider, new SessionModelPreferences()), preferences);
    _ = await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter");

    await vm.ApplyModelChoiceAsync("anthropic/claude");

    AgentSessionViewModel tab = vm.SelectedTab!.ViewModel;
    Assert.Equal("anthropic/claude", tab.Status.ModelId);
    Assert.Contains(tab.Transcript.Entries.OfType<NoticeEntry>(),
        n => n.Text.Contains("anthropic/claude", StringComparison.Ordinal));
    (string key, string value) = Assert.Single(preferences.Writes,
        w => w.Key.StartsWith("model_choice:", StringComparison.Ordinal));
    Assert.Equal($"model_choice:openrouter:{Path.GetFullPath(@"C:\work\alpha")}", key);
    Assert.Equal("anthropic/claude", value);
  }

  [Fact]
  public async Task ApplyModelChoice_Auto_Clears_Session_And_Deletes_Persisted_Choice()
  {
    FakePreferenceStore preferences = new();
    MainViewModel vm = CreateShell((root, provider) => FakeSession(
        root, provider, new SessionModelPreferences { ModelId = "anthropic/claude" }), preferences);
    _ = await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter");

    await vm.ApplyModelChoiceAsync(null);

    AgentTabViewModel tab = vm.SelectedTab!;
    Assert.Null(tab.Container.Preferences!.ModelId);
    Assert.Equal("test/model", tab.ViewModel.Status.ModelId); // the session's bootstrap model
    string expectedKey = $"model_choice:openrouter:{Path.GetFullPath(@"C:\work\alpha")}";
    Assert.Equal([expectedKey], preferences.Deletions);
    // The open persisted the provider preference; the auto choice added NO model write.
    Assert.DoesNotContain(preferences.Writes,
        w => w.Key.StartsWith("model_choice:", StringComparison.Ordinal));
  }

  [Fact]
  public async Task ApplyModelChoice_Without_Tab_Is_A_NoOp()
  {
    MainViewModel vm = CreateShell();

    await vm.ApplyModelChoiceAsync("anthropic/claude");

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
    Assert.Equal("anthropic/claude", tab.Container.Preferences!.ModelId);
    Assert.Equal("anthropic/claude", tab.ViewModel.Status.ModelId);
    // The restore is silent — no notice before any turn has run.
    Assert.DoesNotContain(tab.ViewModel.Transcript.Entries, e => e is NoticeEntry);
  }

  [Fact]
  public async Task OpenAgent_Leaves_No_Choice_Restored_When_None_Persisted()
  {
    FakePreferenceStore preferences = new();
    MainViewModel vm = CreateShell((root, provider) => FakeSession(
        root, provider, new SessionModelPreferences()), preferences);

    _ = await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter");

    AgentTabViewModel tab = vm.SelectedTab!;
    Assert.Null(tab.Container.Preferences!.ModelId);
    Assert.Equal("test/model", tab.ViewModel.Status.ModelId);
  }

  // ── Effort picker ─────────────────────────────────────────────────────────

  [Fact]
  public async Task ChooseEffortCommand_Gated_On_Selected_Tab_And_Raises_Request()
  {
    MainViewModel vm = CreateShell((root, provider) => FakeSession(
        root, provider, new SessionModelPreferences()));
    bool raised = false;
    vm.EffortPickerRequested += (_, _) => raised = true;

    Assert.False(vm.HasSelectedTab);
    Assert.False(vm.ChooseEffortCommand.CanExecute(null));

    _ = await vm.OpenAgentAsync(@"C:\work\alpha", "openrouter");

    Assert.True(vm.HasSelectedTab);
    Assert.True(vm.ChooseEffortCommand.CanExecute(null));
    vm.ChooseEffortCommand.Execute(null);
    Assert.True(raised);
  }

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

  [Fact]
  public async Task ApplySettings_Persists_And_Applies_The_Commit_Style()
  {
    FakePreferenceStore preferences = new();
    MainViewModel vm = CreateSettingsShell(Settings(zai: "zai-key"), preferences, new FakeKeyProtector());

    await vm.ApplySettingsAsync(new SettingsUpdate(null, "zai-key", ZaiEndpointMode.CodingPlan, CommitStyle.Gitmoji));

    Assert.Equal(
        [
            (ZaiSettings.PreferenceKey, "protected:zai-key"),
            (ZaiSettings.EndpointModePreferenceKey, "coding"),
            (AppPreferenceCommitStyleProvider.PreferenceKey, "Gitmoji"),
        ],
        preferences.Writes);
  }
}
