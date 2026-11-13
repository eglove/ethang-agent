using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>The retention tier in Model Settings, and the status line that names it.
///     <para>THE DEFAULT IS THE FEATURE: a session that never opens the dialog must still route
///     under the strict tier, and the only way to move off it is an explicit loosening. These
///     tests pin the default, pin the dropdown's preselection for all three tiers, pin that a
///     loosening travels as a real choice, and pin that the status line names whichever tier is
///     in force rather than only the restrictive ones.</para></summary>
public class RetentionSettingsTests
{
  private const string ProvidersOpenRouter = "openrouter";
  private const string ShellRoot = @"C:\work\retention-settings";

  private static string ModelChoiceKey =>
      $"model_provider_choice:{ProvidersOpenRouter}:{Path.GetFullPath(ShellRoot)}";

  private static ModelProviderEntry Entry(string modelId) => new(
      modelId, "ProviderA", 0.000003m, null, 0.000015m, 200_000, 8192,
      SupportsToolUse: true, SupportsVision: false, SupportsTextOutput: true,
      IntelligenceScore: null, CodingScore: null, AgenticScore: null, LatencyMs: null,
      ThroughputTokensPerSec: null, Description: null);

  private static Func<CancellationToken, Task<Result<IReadOnlyList<ModelProviderEntry>>>> Loader(
      params ModelProviderEntry[] entries)
      => _ => Task.FromResult(Result.Success<IReadOnlyList<ModelProviderEntry>>(entries));

  private static ModelSettingsViewModel Settings(SessionModelPreferences live,
      Action<ModelSettingsSnapshot> persist, RetentionPolicy? currentRetention = null)
      => new(live, ProvidersOpenRouter, persist,
          modelSection: new ModelSectionSeams(Loader(Entry("alpha/model")), "alpha/model",
              CurrentRetention: currentRetention ?? RetentionPolicy.ZeroDataRetention));

  // ── the dropdown: three rows, and the tier it opens on ─────────────────────

  [Fact]
  public void TheDropdown_OffersTheThreeTiers_InTheDocumentedOrder()
  {
    ModelSettingsViewModel vm = Settings(new SessionModelPreferences(), _ => { });

    IReadOnlyList<RetentionOption> options = vm.ModelSection!.RetentionOptions;

    Assert.Equal(
        [RetentionPolicy.ZeroDataRetention, RetentionPolicy.SafetyRetention,
         RetentionPolicy.RetentionAndTraining],
        options.Select(option => option.Policy));
    Assert.Equal(
        ["ZDR", "Safety Retention", "Retention and Training"],
        options.Select(option => option.DisplayName));
  }

  [Fact]
  public void TheCaption_StatesWhatEachTierPromises_AndWhichAreEnforced()
  {
    // The caption is the ONLY place the tiers are explained: the rows are bare names, so a
    // caption that drifted from the tiers would leave the dropdown a mystery box.
    Assert.Equal(
        "ZDR keeps nothing after processing. Safety Retention may keep prompts only to detect "
        + "and prevent abuse, never for training. Retention and Training places no restriction, "
        + "so the provider's own policy applies and may include training. The first two are "
        + "enforced: a model with no eligible endpoint fails the turn rather than routing to a "
        + "provider that keeps your prompts. Whether Safety Retention endpoints are admitted "
        + "also depends on your OpenRouter account setting.",
        ModelPickerViewModel.RetentionCaption);
  }

  [Theory]
  [InlineData(RetentionPolicy.ZeroDataRetention)]
  [InlineData(RetentionPolicy.SafetyRetention)]
  [InlineData(RetentionPolicy.RetentionAndTraining)]
  public async Task TheDropdown_OpensOnTheSessionsTier(RetentionPolicy current)
  {
    ModelSettingsViewModel vm = Settings(new SessionModelPreferences(), _ => { }, current);

    await vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);

    Assert.Equal(current, vm.ModelSection!.Retention);
    Assert.Equal(current, vm.ModelSection.SelectedRetentionOption.Policy);
  }

  [Fact]
  public async Task TheDropdown_OpensOnZdr_ForASessionThatNeverStatedATier()
  {
    // The seams default is the strict tier, which is what an unstated policy means.
    ModelSettingsViewModel vm = new(new SessionModelPreferences(), ProvidersOpenRouter, _ => { },
        modelSection: new ModelSectionSeams(Loader(Entry("alpha/model")), "alpha/model"));

    await vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);

    Assert.Equal(RetentionPolicy.ZeroDataRetention, vm.ModelSection!.Retention);
  }

  // ── the save: the tier is part of the choice ───────────────────────────────

  [Fact]
  public async Task OpeningAndSaving_WithoutTouchingAnything_CarriesUnchanged()
  {
    // The default must survive an untouched open-and-save: a save that reported a change here
    // would rewrite the stored choice on every glance at the dialog.
    SessionModelPreferences live = new();
    ModelSettingsSnapshot? received = null;
    ModelSettingsViewModel vm = Settings(live, s => received = s);
    await vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);

    bool saved = vm.TrySave(out string? error);

    Assert.True(saved, error ?? "no error");
    Assert.Equal(ModelChoiceKind.Unchanged, received!.Model!.Kind);
    Assert.Equal(RetentionPolicy.ZeroDataRetention, live.Retention);
  }

  [Fact]
  public async Task MovingOnlyTheDropdown_IsARealChoice_EvenWithTheModelUntouched()
  {
    // The tier is an independent axis of the same decision. A user who only moves the dropdown
    // has made a decision, and reporting Unchanged would discard it silently.
    SessionModelPreferences live = new();
    ModelSettingsSnapshot? received = null;
    ModelSettingsViewModel vm = Settings(live, s => received = s);
    await vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);
    vm.ModelSection!.Retention = RetentionPolicy.SafetyRetention;

    bool saved = vm.TrySave(out string? error);

    Assert.True(saved, error ?? "no error");
    Assert.Equal(ModelChoiceKind.Pinned, received!.Model!.Kind);
    Assert.Equal("alpha/model", received.Model.ModelId);
    Assert.Equal(RetentionPolicy.SafetyRetention, received.Model.Retention);
    Assert.Equal(RetentionPolicy.SafetyRetention, live.Retention);
  }

  [Theory]
  [InlineData(RetentionPolicy.ZeroDataRetention, RetentionPolicy.SafetyRetention)]
  [InlineData(RetentionPolicy.ZeroDataRetention, RetentionPolicy.RetentionAndTraining)]
  [InlineData(RetentionPolicy.RetentionAndTraining, RetentionPolicy.ZeroDataRetention)]
  [InlineData(RetentionPolicy.SafetyRetention, RetentionPolicy.RetentionAndTraining)]
  public async Task MovingTheDropdown_ToAnyOtherTier_IsARealChoice(RetentionPolicy from, RetentionPolicy to)
  {
    SessionModelPreferences live = new();
    ModelSettingsSnapshot? received = null;
    ModelSettingsViewModel vm = Settings(live, s => received = s, from);
    await vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);

    vm.ModelSection!.Retention = to;
    Assert.True(vm.TrySave(out string? error), error);

    Assert.Equal(ModelChoiceKind.Pinned, received!.Model!.Kind);
    Assert.Equal(to, received.Model.Retention);
    Assert.Equal(to, live.Retention);
  }

  [Fact]
  public async Task SavingTheLoosenedTier_SerializesNoProviderSettings()
  {
    // The tier rides the model choice, not the opaque OpenRouter payload: an all-default
    // provider section still saves null, so loosening cannot resurrect a stale settings blob.
    ModelSettingsSnapshot? received = null;
    ModelSettingsViewModel vm = Settings(new SessionModelPreferences(), s => received = s);
    await vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);
    vm.ModelSection!.Retention = RetentionPolicy.RetentionAndTraining;

    _ = vm.TrySave(out _);

    Assert.Null(received!.ProviderSettingsJson);
  }

  [Fact]
  public void AHostWithNoModelSection_KeepsTheSessionsOwnTier()
  {
    // No section means no dropdown, so the save states nothing new: the session's tier survives
    // rather than being reset to the default by a window that never showed it.
    SessionModelPreferences live = new() { Retention = RetentionPolicy.RetentionAndTraining };
    ModelSettingsViewModel vm = new(live, ProvidersOpenRouter, _ => { });

    Assert.True(vm.TrySave(out string? error), error);

    Assert.Equal(RetentionPolicy.RetentionAndTraining, live.Retention);
  }

  // ── the status line: every tier is named ───────────────────────────────────

  [Theory]
  [InlineData(RetentionPolicy.ZeroDataRetention, "no data retention")]
  [InlineData(RetentionPolicy.SafetyRetention, "safety retention only")]
  [InlineData(RetentionPolicy.RetentionAndTraining, "retention and training allowed")]
  public void TheStatusLine_NamesTheTierInForce(RetentionPolicy tier, string badge)
  {
    SessionModelPreferences preferences = new();
    AgentSessionViewModel vm = Session(preferences);

    vm.ApplyModelChoice("anthropic/claude", ModelVariant.Standard, tier);

    Assert.Equal(badge, vm.Status.ServingRetention);
    Assert.Equal(tier, preferences.Retention);
  }

  [Fact]
  public void AFreshSession_NamesTheStrictTier_BeforeAnyChoiceIsMade()
  {
    // A session that never opens the dialog still states what the provider may keep: the bar
    // is not silent until the picker runs.
    AgentSessionViewModel vm = Session(new SessionModelPreferences());

    Assert.Equal("no data retention", vm.Status.ServingRetention);
  }

  [Fact]
  public void AFreshSession_NamesTheTierItsPreferencesAlreadyCarry()
  {
    AgentSessionViewModel vm = Session(new SessionModelPreferences
    {
      Retention = RetentionPolicy.RetentionAndTraining,
    });

    Assert.Equal("retention and training allowed", vm.Status.ServingRetention);
  }

  [Fact]
  public void AChoiceThatNamesOnlyAModel_LeavesTheTierAlone()
  {
    // A caller that states no tier is not loosening one: the session keeps what it had.
    SessionModelPreferences preferences = new() { Retention = RetentionPolicy.RetentionAndTraining };
    AgentSessionViewModel vm = Session(preferences);

    vm.ApplyModelChoice("anthropic/claude");

    Assert.Equal(RetentionPolicy.RetentionAndTraining, preferences.Retention);
    Assert.Equal("retention and training allowed", vm.Status.ServingRetention);
  }

  // ── persistence: the tier round-trips through the shell ────────────────────

  [Fact]
  public async Task SavingOnlyTheDropdown_PersistsTheTier_AndTheStatusLineNamesIt()
  {
    // The one path that matters end to end: a window that moves nothing but the dropdown must
    // write the tier, land it on the live preferences, and be visible on the bar.
    FakePreferenceStore store = new();
    SessionModelPreferences live = new();
    MainViewModel shell = CreateShell(store, live);
    _ = await OpenShellAsync(shell, ShellRoot);

    ModelSettingsSnapshot? captured = null;
    ModelSettingsViewModel vm = Settings(live, snapshot => captured = snapshot);
    await vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);
    vm.ModelSection!.Retention = RetentionPolicy.SafetyRetention;
    Assert.True(vm.TrySave(out string? error), error);

    await shell.ApplySamplingSettingsAsync(captured!).ConfigureAwait(true);

    ModelVariantChoice stored = ModelVariantChoice.Parse(store.Stored[ModelChoiceKey]).Value!;
    Assert.Equal(RetentionPolicy.SafetyRetention, stored.Retention);
    Assert.Equal(RetentionPolicy.SafetyRetention, live.Retention);
    Assert.Equal("safety retention only", shell.SelectedTab!.ViewModel.Status.ServingRetention);
  }

  [Fact]
  public async Task AnUntouchedSave_WritesNoModelChoice()
  {
    FakePreferenceStore store = new();
    SessionModelPreferences live = new();
    MainViewModel shell = CreateShell(store, live);
    _ = await OpenShellAsync(shell, ShellRoot);

    ModelSettingsSnapshot? captured = null;
    ModelSettingsViewModel vm = Settings(live, snapshot => captured = snapshot);
    await vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);
    Assert.True(vm.TrySave(out string? error), error);
    await shell.ApplySamplingSettingsAsync(captured!).ConfigureAwait(true);

    Assert.Equal(ModelChoiceKind.Unchanged, captured!.Model!.Kind);
    Assert.DoesNotContain(store.Writes, w => w.Key == ModelChoiceKey);
  }

  [Fact]
  public async Task AReopenedWorkspace_RestoresTheStoredTier_AndTheStatusLineNamesIt()
  {
    FakePreferenceStore store = new();
    store.Stored[ModelChoiceKey] = new ModelVariantChoice(
        "anthropic/claude", ModelVariant.Standard, RetentionPolicy.RetentionAndTraining).Serialize();
    SessionModelPreferences restored = new();
    MainViewModel shell = CreateShell(store, restored);

    AgentTabViewModel tab = await OpenShellAsync(shell, ShellRoot).ConfigureAwait(true);

    Assert.Equal(RetentionPolicy.RetentionAndTraining, restored.Retention);
    Assert.Equal("retention and training allowed", tab.ViewModel.Status.ServingRetention);
  }

  // ── helpers ───────────────────────────────────────────────────────────────

  private static AgentSessionViewModel Session(SessionModelPreferences preferences) => new(
      (_, _, _, _) => Task.FromResult(Result.Success("")),
      new RecordingLifecycle(new StubStore()), AgentId.NewId(), new Conversation(),
      "OpenRouter", "openrouter/auto",
      new AgentSessionViewModelOptions { WorkspaceRoot = @"C:\work\demo", ModelPreferences = preferences });

  private static MainViewModel CreateShell(IAppPreferenceStore preferences, SessionModelPreferences sessionPreferences)
      => new((root, provider) => Task.FromResult(Result.Success(BuildSession(root, provider, sessionPreferences))),
          new MainViewModelOptions { Preferences = preferences });

  private static async Task<AgentTabViewModel> OpenShellAsync(MainViewModel shell, string root)
  {
    Result<AgentTabViewModel> opened = await shell.OpenAgentAsync(root, ProvidersOpenRouter).ConfigureAwait(true);
    Assert.True(opened.IsSuccess, $"session open failed: [{opened.Error?.Code}] {opened.Error?.Message}");
    return opened.Value;
  }

  private static AgentSession BuildSession(string root, string provider, SessionModelPreferences preferences)
  {
    // A session whose container these tests never dispose - built via a throwaway
    // ServiceCollection so DisposeAsync stays legal (the shell-test shape).
    ServiceProvider services = new ServiceCollection().BuildServiceProvider();
    return new AgentSession(
        services,
        AgentId.NewId(),
        new Conversation(),
        Handler: null!,
        Lifecycle: new RootSessionLifecycle(new StubStore()),
        Model: ModelConfig.Create("test/model", 128, 0.1f, 8192).Value!,
        WorkspaceRoot: root,
        ProviderName: provider,
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
}
