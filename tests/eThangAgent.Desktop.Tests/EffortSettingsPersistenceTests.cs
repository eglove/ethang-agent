using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>The Model Settings window's effort choice must persist through the SAME
///     effort_choice key the effort picker uses, and a saved Model default must
///     clear a previously persisted level instead of letting the stale stored
///     value win on reopen (final-review MAJOR). Also: the session view-model's
///     apply must carry ReasoningEffort and the status bar's effort cell.
///     </summary>
public class EffortSettingsPersistenceTests
{
  private const string ProvidersOpenRouter = "openrouter";
  private const string ShellRoot = @"C:\work\effort-settings-roundtrip";

  private static string EffortKey => $"effort_choice:{ProvidersOpenRouter}:{ShellRoot}";

  // ── the session view-model applies the window's effort choice ──

  [Fact]
  public void ApplySamplingSettings_CarriesEffortOntoPreferenceAndStatus()
  {
    SessionModelPreferences preferences = new() { ReasoningEffort = ReasoningEffort.High };
    AgentSessionViewModel vm = new(
        (_, _, _, _) => Task.FromResult(Result.Success("")),
        new RecordingLifecycle(new StubStore()), AgentDomain.AgentId.NewId(), new ConversationDomain.Conversation(),
        "OpenRouter", "test/model",
        new AgentSessionViewModelOptions { WorkspaceRoot = @"C:\work\demo", ModelPreferences = preferences });
    Assert.Equal("High", vm.Status.Effort); // seeded preference drives the initial status

    vm.ApplySamplingSettings(new SessionModelPreferences { Temperature = 0.3f }); // Model default

    Assert.Null(preferences.ReasoningEffort);
    Assert.Equal("Model default", vm.Status.Effort);
    Assert.Equal(0.3f, preferences.Temperature); // the knobs still apply
    NoticeEntry notice = Assert.IsType<NoticeEntry>(vm.Transcript.Entries[^1]);
    Assert.Contains("next turn", notice.Text, StringComparison.Ordinal);
  }

  [Fact]
  public void ApplySamplingSettings_WithoutPreferences_NoticesUnavailable()
  {
    AgentSessionViewModel vm = new(
        (_, _, _, _) => Task.FromResult(Result.Success("")),
        new RecordingLifecycle(new StubStore()), AgentDomain.AgentId.NewId(), new ConversationDomain.Conversation(),
        "OpenRouter", "test/model",
        new AgentSessionViewModelOptions { WorkspaceRoot = @"C:\work\demo" });

    vm.ApplySamplingSettings(new SessionModelPreferences { ReasoningEffort = ReasoningEffort.Low });

    NoticeEntry notice = Assert.IsType<NoticeEntry>(vm.Transcript.Entries[^1]);
    Assert.Contains("unavailable", notice.Text, StringComparison.Ordinal);
  }

  // ── the settings-save path persists effort; reopen restores it ──

  [Theory]
  [InlineData("High")]
  [InlineData("default")]
  public async Task SettingsWindowEffortChoice_PersistsAndRestores(string effortChoice)
  {
    FakePreferenceStore store = new();
    store.Stored[EffortKey] = "High"; // a previously persisted picker choice
    SessionModelPreferences first = new();
    MainViewModel shell = CreateSettingsShell(store, first);
    _ = await OpenShellAsync(shell, ShellRoot, ProvidersOpenRouter).ConfigureAwait(true);
    Assert.Equal(ReasoningEffort.High, first.ReasoningEffort); // the stale choice restored for the run

    SessionModelPreferences live = new() { ReasoningEffort = ReasoningEffort.High };
    ModelSettingsSnapshot? captured = null;
    ModelSettingsViewModel vm = new(live, ProvidersOpenRouter, persist: snapshot => captured = snapshot)
    {
      EffortChoice = effortChoice,
    };
    bool saved = vm.TrySave(out string? error);
    Assert.True(saved, error ?? "no error");
    ModelSettingsSnapshot savedSnapshot = captured ?? throw new InvalidOperationException("save never invoked the persistence seam");

    await shell.ApplySamplingSettingsAsync(savedSnapshot).ConfigureAwait(true);

    if (effortChoice == "default")
    {
      // A saved Model default must DELETE the stored level — never leave a stale
      // value that silently wins over the user's explicit choice on reopen.
      Assert.Contains(EffortKey, store.Deletions);
      Assert.False(store.Stored.ContainsKey(EffortKey), "a saved Model default must not leave the stale effort_choice behind");
    }
    else
    {
      Assert.Equal("High", store.Stored[EffortKey]);
    }

    SessionModelPreferences restored = new();
    MainViewModel reopened = CreateSettingsShell(store, restored);
    _ = await OpenShellAsync(reopened, ShellRoot, ProvidersOpenRouter).ConfigureAwait(true);

    Assert.Equal(effortChoice == "default" ? null : ReasoningEffort.High, restored.ReasoningEffort);
    if (effortChoice == "default")
    {
      Assert.Null(reopened.Tabs[0].ViewModel.Status.Effort == "Model default" ? null : restored);
      Assert.Equal("Model default", reopened.Tabs[0].ViewModel.Status.Effort);
    }
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

  private static MainViewModel CreateSettingsShell(IAppPreferenceStore preferences, SessionModelPreferences sessionPreferences)
      => new((root, provider) => Task.FromResult(Result.Success(BuildSession(root, provider, sessionPreferences))),
          new MainViewModelOptions { Preferences = preferences });

  private static async Task<AgentTabViewModel> OpenShellAsync(MainViewModel shell, string root, string provider)
  {
    Result<AgentTabViewModel> opened = await shell.OpenAgentAsync(root, provider).ConfigureAwait(true);
    Assert.True(opened.IsSuccess, $"session open failed: [{opened.Error?.Code}] {opened.Error?.Message}");
    return opened.Value;
  }

  private static AgentSession BuildSession(string root, string provider, SessionModelPreferences preferences)
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
        Preferences: preferences);
  }
}
