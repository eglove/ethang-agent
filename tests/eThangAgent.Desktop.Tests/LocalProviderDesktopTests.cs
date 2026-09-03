using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using eThangAgent.Zai.ACL;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>The desktop shell's local-provider behaviors (Task 10): the effort
///     picker is gated OFF on a local tab, saving the settings modal persists the
///     base URL plain and the key DPAPI-protected, the provider dropdown gains the
///     local row only when a base URL is set, and startup loads both local
///     preferences back onto the settings snapshot. Session creation is faked at
///     the factory seam, mirroring ShellViewModelTests.</summary>
public class LocalProviderDesktopTests
{
  private static AgentSession FakeSession(string root, string provider = "openrouter",
      SessionModelPreferences? preferences = null)
  {
    ServiceProvider services = new ServiceCollection().BuildServiceProvider();
    return new AgentSession(
        services,
        AgentId.NewId(),
        new ConversationDomain.Conversation(),
        Handler: null!,
        Lifecycle: new RootSessionLifecycle(new TestFixtures.StubStore()),
        Model: ModelConfig.Create("test/model", null, 128, 0.1f, 8192).Value!,
        WorkspaceRoot: root,
        ProviderName: provider,
        ClarifyChannel: null!,
        Inbox: new BoundedAgentMailbox(),
        ChildRuntime: new TestFixtures.StubAgentRuntime(),
        Preferences: preferences);
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

  private sealed class FakeKeyProtector : IApiKeyProtector
  {
    public string Protect(string apiKey) => $"protected:{apiKey}";

    public string? Unprotect(string storedValue)
        => storedValue.StartsWith("protected:", StringComparison.Ordinal)
            ? storedValue["protected:".Length..]
            : null;
  }

  private static AgentSettings ShellSettings(string? baseUrl, string? apiKey = null,
      string? openRouterKey = null, string? zaiKey = null) => new(
      new OpenRouterSettings(openRouterKey, new Uri("https://openrouter.test")),
      new ZaiSettings(zaiKey, new Uri("https://zai.test")),
      new SubAgentOptions(null, 2),
      Local: new LocalSettings(baseUrl, apiKey));

  private static MainViewModel CreateSettingsShell(AgentSettings settings,
      IAppPreferenceStore? preferences = null, IApiKeyProtector? protector = null,
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

  private static SettingsUpdate Update(string? localBaseUrl, string? localApiKey,
      string? openRouterKey = null, string? zaiKey = null)
      => new(openRouterKey, zaiKey, ZaiEndpointMode.CodingPlan, CommitStyle.Conventional,
          LocalBaseUrlText: localBaseUrl, LocalApiKey: localApiKey);

  // ── Effort gate ───────────────────────────────────────────────────────────

  [Fact]
  public async Task ChooseEffortCommand_Is_Gated_Off_On_A_Local_Tab()
  {
    MainViewModel vm = new((root, provider) => Task.FromResult(Result.Success(
        FakeSession(root, provider, new SessionModelPreferences()))));

    _ = await vm.OpenAgentAsync(@"C:\work\alpha", Providers.Local);

    Assert.True(vm.HasSelectedTab);
    Assert.False(vm.ChooseEffortCommand.CanExecute(null));

    bool raised = false;
    vm.EffortPickerRequested += (_, _) => raised = true;
    vm.RequestChooseEffort(); // the shell entry point shares the command's gate
    Assert.False(raised);
  }

  [Fact]
  public async Task Effort_Gate_Requeries_When_Selection_Moves_Between_Local_And_NonLocal_Tabs()
  {
    MainViewModel vm = new((root, provider) => Task.FromResult(Result.Success(
        FakeSession(root, provider, new SessionModelPreferences()))));

    _ = await vm.OpenAgentAsync(@"C:\work\alpha", Providers.Local);
    _ = await vm.OpenAgentAsync(@"C:\work\beta", Providers.OpenRouter);
    AgentTabViewModel localTab = vm.Tabs[0];
    AgentTabViewModel openRouterTab = vm.Tabs[1];

    Assert.True(vm.ChooseEffortCommand.CanExecute(null)); // beta (openrouter) selected

    vm.SelectedTab = localTab;
    Assert.False(vm.ChooseEffortCommand.CanExecute(null));

    vm.SelectedTab = openRouterTab;
    Assert.True(vm.ChooseEffortCommand.CanExecute(null));
  }

  [Fact]
  public async Task Model_Picker_Stays_Available_On_A_Local_Tab()
  {
    MainViewModel ungated = new((root, provider) => Task.FromResult(Result.Success(
        FakeSession(root, provider, new SessionModelPreferences()))));
    _ = await ungated.OpenAgentAsync(@"C:\work\alpha", Providers.Local);
    Assert.True(ungated.ChooseModelCommand.CanExecute(null));
  }

  // ── Settings save: persistence + provider surface ─────────────────────────

  [Fact]
  public async Task ApplySettings_Persists_Local_Base_Url_Plain_And_Key_Protected()
  {
    FakePreferenceStore preferences = new();
    MainViewModel vm = CreateSettingsShell(ShellSettings(null), preferences, new FakeKeyProtector());

    await vm.ApplySettingsAsync(Update("  http://localhost:1234  ", "lm-key"));

    Assert.Equal(
        [
          (LocalSettings.BaseUrlPreferenceKey, "http://localhost:1234"),
          (LocalSettings.PreferenceKey, "protected:lm-key"),
          (ZaiSettings.EndpointModePreferenceKey, "coding"),
          (AppPreferenceCommitStyleProvider.PreferenceKey, "Conventional"),
        ],
        preferences.Writes);
    // The cleared provider keys delete; the freshly set local key does not.
    Assert.Equal([OpenRouterSettings.PreferenceKey, ZaiSettings.PreferenceKey], preferences.Deletions);
    Assert.Equal(["local"], vm.AvailableProviders.Select(p => p.Id)); // no provider keys configured
    Assert.True(vm.HasConfiguredProvider);
  }

  [Fact]
  public async Task ApplySettings_Blank_Local_Base_Url_Deletes_The_Preference_And_Drops_The_Row()
  {
    FakePreferenceStore preferences = new();
    MainViewModel vm = CreateSettingsShell(
        ShellSettings("http://localhost:1234", "lm-key", openRouterKey: "sk-or", zaiKey: "zai-key"),
        preferences, new FakeKeyProtector(), preferredProviderId: Providers.Local);
    Assert.Equal(["openrouter", "zai", "local"], vm.AvailableProviders.Select(p => p.Id));
    Assert.Equal(Providers.Local, vm.PreferredProviderId); // preselected local stays valid

    // The update carries FULL field state — the dialog sends every field, so the
    // two provider keys ride along unchanged while the local pair is cleared.
    await vm.ApplySettingsAsync(Update(null, null, openRouterKey: "sk-or", zaiKey: "zai-key"));

    // URL first (plain), key second (protected) — the same order the keys use.
    Assert.Equal(
        [LocalSettings.BaseUrlPreferenceKey, LocalSettings.PreferenceKey],
        preferences.Deletions); // the blank local fields clear exactly their own pair
    Assert.Equal(["openrouter", "zai"], vm.AvailableProviders.Select(p => p.Id));
    Assert.Equal(Providers.OpenRouter, vm.PreferredProviderId); // falls back to the first remaining row
  }

  [Fact]
  public async Task ApplySettings_Carries_The_Local_Fields_Alongside_The_Provider_Keys()
  {
    FakePreferenceStore preferences = new();
    MainViewModel vm = CreateSettingsShell(
        ShellSettings("http://localhost:1234"), preferences, new FakeKeyProtector());

    await vm.ApplySettingsAsync(Update("http://localhost:9999", "new-lm-key",
        openRouterKey: "sk-or-v1-abc", zaiKey: "zai-key"));

    Assert.Equal(
        [
          (OpenRouterSettings.PreferenceKey, "protected:sk-or-v1-abc"),
          (ZaiSettings.PreferenceKey, "protected:zai-key"),
          (LocalSettings.BaseUrlPreferenceKey, "http://localhost:9999"),
          (LocalSettings.PreferenceKey, "protected:new-lm-key"),
          (ZaiSettings.EndpointModePreferenceKey, "coding"),
          (AppPreferenceCommitStyleProvider.PreferenceKey, "Conventional"),
        ],
        preferences.Writes);
  }

  [Fact]
  public async Task PrepareAsync_Loads_The_Stored_Local_Base_Url_And_Protected_Key_Back_Onto_Settings()
  {
    string dbPath = Path.Combine(Path.GetTempPath(), "ethang-t10-load-" + Guid.NewGuid().ToString("N"), "test.db");
    try
    {
      // Seed the store exactly the way a prior modal save leaves it: plain URL,
      // DPAPI-protected key (the real protector — startup unprotects with it).
      AppDatabase seed = new(dbPath);
      SqliteAppPreferenceStore store = new(seed);
      _ = await store.SetAsync(LocalSettings.BaseUrlPreferenceKey, "http://localhost:1234",
          TestContext.Current.CancellationToken);
      _ = await store.SetAsync(LocalSettings.PreferenceKey,
          new DpapiKeyProtector().Protect("lm-key"), TestContext.Current.CancellationToken);

      Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", dbPath);
      try
      {
        DesktopBootstrap boot = await DesktopHost.PrepareAsync();

        Assert.True(boot.Settings.HasLocal);
        Assert.Equal("http://localhost:1234", boot.Settings.Local!.BaseUrlText);
        Assert.Equal("lm-key", boot.Settings.Local.ApiKey);
        // The fresh store holds no provider keys: neither keyed provider is offered,
        // so local is the only configured row.
        Assert.False(boot.Settings.HasOpenRouter);
        Assert.False(boot.Settings.HasZai);
      }
      finally
      {
        Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", null);
      }
    }
    finally
    {
      // Connection pooling keeps the file open after the stores are done with it.
      Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
      Directory.Delete(Path.GetDirectoryName(dbPath)!, true);
    }
  }

  [Fact]
  public async Task Configured_Local_Fields_Are_The_Settings_Modal_Prefill_Source()
  {
    MainViewModel configured = CreateSettingsShell(
        ShellSettings("http://localhost:1234", "lm-key"));
    Assert.Equal("http://localhost:1234", configured.ConfiguredLocalBaseUrl);
    Assert.Equal("lm-key", configured.ConfiguredLocalApiKey);

    MainViewModel unconfigured = CreateSettingsShell(ShellSettings(null));
    Assert.Null(unconfigured.ConfiguredLocalBaseUrl);
    Assert.Null(unconfigured.ConfiguredLocalApiKey);

    // After a save the exposure tracks the new snapshot — the next open of the
    // modal prefills what was just confirmed, like the provider keys do.
    await configured.ApplySettingsAsync(Update("http://localhost:9999", "new-key"));
    Assert.Equal("http://localhost:9999", configured.ConfiguredLocalBaseUrl);
    Assert.Equal("new-key", configured.ConfiguredLocalApiKey);
  }
}
