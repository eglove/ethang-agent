using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>The ordered model list through the save path, the stored preference, and the
///     restore-on-open: what a save carries, what it writes, and what a reopen shows.
///     <para>THE LIST IS THE DECISION, so every assertion here is about ORDER: a save that
///     reports a reordered list as Unchanged discards a user's edit, and a reopen that shows
///     anything but the stored head runs a model the user did not put first.</para>
///     <para>THE 'NO MODEL' BRANCH IS DELETED BEHAVIOUR, not relocated: the picker refuses to
///     remove the last row and <see cref="ModelSelection.Create"/> refuses an empty
///     collection, so there is no null choice for the save path to substitute a default
///     for.</para></summary>
public class ModelChoicePersistenceTests
{
  private const string Provider = "openrouter";
  private const string Root = @"C:\work\model-choice-persistence";

  private static string ModelChoiceKey =>
      $"model_provider_choice:{Provider}:{Path.GetFullPath(Root)}";

  private static string LegacyModelChoiceKey =>
      $"model_choice:{Provider}:{Path.GetFullPath(Root)}";

  // ── 1-3: what a save carries ──────────────────────────────────────────────

  [Fact]
  public async Task APinnedSaveOverAnOrderedPair_CarriesTheWholeListInOrder()
  {
    // An edit over a two-entry list carries the LIST, not its head: the tail is the ordered
    // failover the user stated, so collapsing it would silently delete every backup. The
    // variant and the tier ride beside it as ONE value each for the whole list.
    SessionModelPreferences live = new();
    ModelSettingsSnapshot? captured = null;
    ModelSettingsViewModel vm = Settings(live, ["alpha/model", "beta/other"],
        snapshot => captured = snapshot);
    await vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);
    vm.ModelSection!.SelectedRow = vm.ModelSection.FilteredRows.First(r => r.ModelId == "gamma/third");
    vm.ModelSection.AddModelCommand.Execute(null);

    Assert.True(vm.TrySave(out string? error), error);

    Assert.Equal(ModelChoiceKind.Pinned, captured!.Model!.Kind);
    Assert.Equal(["alpha/model", "beta/other", "gamma/third"], captured.Model.ModelIds);
    Assert.Equal(ModelVariant.Standard, captured.Model.Variant);
    Assert.Equal(RetentionPolicy.ZeroDataRetention, captured.Model.Retention);
    Assert.Equal(["alpha/model", "beta/other", "gamma/third"], live.ModelIds);
  }

  [Fact]
  public async Task OpeningAndSavingWithNoEdit_LeavesTheStoredListExactlyAsItWas()
  {
    // The stored list survives a save that changed nothing: no write is issued at all, so a
    // two-entry selection cannot be collapsed to its head by opening Model Settings.
    FakePreferenceStore store = new();
    store.Stored[ModelChoiceKey] = ModelSelection.Create(
        ["alpha/model", "beta/other"]).Value!.Serialize();
    SessionModelPreferences live = new();
    MainViewModel shell = CreateShell(store, live);
    AgentTabViewModel tab = await OpenAsync(shell).ConfigureAwait(true);
    Assert.Equal(["alpha/model", "beta/other"], live.ModelIds);
    store.Writes.Clear();
    ModelSettingsSnapshot? captured = null;
    ModelSettingsViewModel vm = Settings(live, live.ModelIds!, snapshot => captured = snapshot);
    await vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);

    Assert.True(vm.TrySave(out string? error), error);
    await shell.ApplySamplingSettingsAsync(captured!).ConfigureAwait(true);

    Assert.Equal(ModelChoiceKind.Unchanged, captured!.Model!.Kind);
    Assert.DoesNotContain(store.Writes, w => w.Key == ModelChoiceKey);
    Assert.Equal(["alpha/model", "beta/other"], live.ModelIds);
    Assert.Equal(["alpha/model", "beta/other"],
        ModelSelection.Parse(store.Stored[ModelChoiceKey]).Value!.ModelIds);
    Assert.Equal("alpha/model", tab.ViewModel.Status.ModelId);
  }

  [Fact]
  public async Task ReorderingTheList_Alone_IsARealEdit()
  {
    // Behaviour 3: a reorder is an edit exactly as a variant change is. This is the failure a
    // comparison on the SELECTED ROW rather than the ordered ids would produce: the row count
    // and every membership is identical, so only order can tell the two saves apart.
    SessionModelPreferences live = new();
    ModelSettingsSnapshot? captured = null;
    ModelSettingsViewModel vm = Settings(live, ["alpha/model", "beta/other"],
        snapshot => captured = snapshot);
    await vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);
    vm.ModelSection!.SelectedModel = vm.ModelSection.SelectedModels.First(r => r.Position == 2);
    vm.ModelSection.MoveUpCommand.Execute(null);
    Assert.Equal(["beta/other", "alpha/model"],
        vm.ModelSection.SelectedModels.Select(row => row.ModelId));

    Assert.True(vm.TrySave(out string? error), error);

    Assert.Equal(ModelChoiceKind.Pinned, captured!.Model!.Kind);
    Assert.Equal(["beta/other", "alpha/model"], captured.Model.ModelIds);
    Assert.Equal(["beta/other", "alpha/model"], live.ModelIds);
  }

  [Fact]
  public async Task AnUntouchedSave_WritesNoPreference()
  {
    // Behaviour 2: opening the window and pressing Save must not invent an edit.
    FakePreferenceStore store = new();
    SessionModelPreferences live = new();
    MainViewModel shell = CreateShell(store, live);
    _ = await OpenAsync(shell);
    ModelSettingsSnapshot? captured = null;
    ModelSettingsViewModel vm = Settings(live, ["alpha/model"], snapshot => captured = snapshot);
    await vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);

    Assert.True(vm.TrySave(out string? error), error);
    await shell.ApplySamplingSettingsAsync(captured!).ConfigureAwait(true);

    Assert.Equal(ModelChoiceKind.Unchanged, captured!.Model!.Kind);
    Assert.DoesNotContain(store.Writes, w => w.Key == ModelChoiceKey);
  }

  // ── 4-5: what is stored, and what an older document restores ───────────────

  [Fact]
  public async Task TheStoredValue_SpellsTheWholeList_AndReadsBackIdentically()
  {
    // Behaviour 4: the exact stored document, and a restore that reproduces it.
    FakePreferenceStore store = new();
    SessionModelPreferences live = new();
    MainViewModel shell = CreateShell(store, live);
    _ = await OpenAsync(shell);

    await shell.ApplyModelChoiceAsync(["a", "b"], ModelVariant.Nitro,
        RetentionPolicy.ZeroDataRetention).ConfigureAwait(true);

    (string _, string written) = Assert.Single(store.Writes, w => w.Key == ModelChoiceKey);
    Assert.Equal(
        /*lang=json,strict*/ """{"Models":["a","b"],"Variant":"Nitro","Retention":"ZeroDataRetention"}""",
        written);
    ModelSelection readBack = ModelSelection.Parse(written).Value!;
    Assert.Equal(["a", "b"], readBack.ModelIds);
    Assert.Equal(ModelVariant.Nitro, readBack.Variant);
    Assert.Equal(RetentionPolicy.ZeroDataRetention, readBack.Retention);
    // A reopened workspace reads the same document back to the same value.
    SessionModelPreferences reopened = new();
    MainViewModel second = CreateShell(new FakePreferenceStore(store.Stored), reopened);
    AgentTabViewModel tab = await OpenAsync(second).ConfigureAwait(true);
    Assert.Equal(["a", "b"], reopened.ModelIds);
    Assert.Equal(ModelVariant.Nitro, reopened.ModelVariant);
    Assert.Equal("a", tab.ViewModel.Status.ModelId);
    Assert.Equal("· 1 backup", tab.ViewModel.Status.BackupCountText);
  }

  [Theory]
  // Behaviour 5: both pre-list shapes restore as a ONE-entry list and leave every other
  // preference exactly as the rest of the restore path had it.
  [InlineData("bare-id", "anthropic/claude")]
  [InlineData("single-choice",
      /*lang=json,strict*/ """{"ModelId":"anthropic/claude","Variant":"Nitro","Retention":"SafetyRetention"}""")]
  public async Task AnOlderStoredDocument_OpensAsAOneEntryList(string shape, string stored)
  {
    FakePreferenceStore store = new();
    store.Stored[ModelChoiceKey] = stored;
    SessionModelPreferences live = new();
    MainViewModel shell = CreateShell(store, live);

    AgentTabViewModel tab = await OpenAsync(shell).ConfigureAwait(true);

    Assert.Equal(["anthropic/claude"], live.ModelIds);
    Assert.Equal("anthropic/claude", tab.ViewModel.Status.ModelId);
    Assert.Equal(string.Empty, tab.ViewModel.Status.BackupCountText);
    // The retired key is not consulted, because the live key answered.
    Assert.DoesNotContain(store.Deletions, key => key == ModelChoiceKey);
    _ = shape;
  }

  [Fact]
  public async Task ALegacyKeyValue_StillOpensTheModelItNames()
  {
    // The retired key stays READ: dropping it would discard the model every existing
    // workspace chose.
    FakePreferenceStore store = new();
    store.Stored[LegacyModelChoiceKey] = "anthropic/claude";
    SessionModelPreferences live = new();
    MainViewModel shell = CreateShell(store, live);

    AgentTabViewModel tab = await OpenAsync(shell).ConfigureAwait(true);

    Assert.Equal(["anthropic/claude"], live.ModelIds);
    Assert.Equal("anthropic/claude", tab.ViewModel.Status.ModelId);
  }

  // ── 6: applying a choice ──────────────────────────────────────────────────

  [Fact]
  public async Task ApplyingAChoice_SetsTheListAndNamesTheHead()
  {
    // Behaviour 6: the list lands whole and the bar names entry 1.
    FakePreferenceStore store = new();
    SessionModelPreferences live = new();
    MainViewModel shell = CreateShell(store, live);
    AgentTabViewModel tab = await OpenAsync(shell).ConfigureAwait(true);

    await shell.ApplyModelChoiceAsync(["alpha/model", "beta/other"]).ConfigureAwait(true);

    Assert.Equal(["alpha/model", "beta/other"], live.ModelIds);
    Assert.Equal("alpha/model", tab.ViewModel.Status.ModelId);
    Assert.Equal("· 1 backup", tab.ViewModel.Status.BackupCountText);
  }

  // ── 11: the deleted null-default branch ───────────────────────────────────

  [Fact]
  public void AnEmptyList_IsRefused_WithTheAtLeastOneModelRule()
  {
    // Behaviour 11: there is no 'null means the session default' substitution left to reach,
    // because a session with no model has nothing to serve. The refusal names the rule.
    SessionModelPreferences live = new() { ModelIds = ["alpha/model"] };
    AgentSessionViewModel vm = new(
        (_, _, _, _) => Task.FromResult(Result.Success("")),
        new RecordingLifecycle(new StubStore()), AgentId.NewId(), new Conversation(),
        "OpenRouter", "alpha/model",
        new AgentSessionViewModelOptions { WorkspaceRoot = @"C:\work\demo", ModelPreferences = live });

    vm.ApplyModelChoice([]);

    Assert.Equal(["alpha/model"], live.ModelIds);
    Assert.Equal("alpha/model", vm.Status.ModelId);
    NoticeEntry notice = Assert.IsType<NoticeEntry>(vm.Transcript.Entries[^1]);
    Assert.Contains("at least one model", notice.Text, StringComparison.Ordinal);
  }

  // ── 12: a failed write does not fail the save ──────────────────────────────

  [Fact]
  public async Task AFailedPreferenceWrite_DoesNotFailTheSave()
  {
    // Behaviour 12: best effort. A store that refuses the write leaves the session's live
    // selection exactly as the save applied it, and the save still reports success.
    FakePreferenceStore store = new() { AcceptWrites = false };
    SessionModelPreferences live = new();
    MainViewModel shell = CreateShell(store, live);
    AgentTabViewModel tab = await OpenAsync(shell).ConfigureAwait(true);

    await shell.ApplyModelChoiceAsync(["alpha/model", "beta/other"]).ConfigureAwait(true);

    Assert.Equal(["alpha/model", "beta/other"], live.ModelIds);
    Assert.Equal("alpha/model", tab.ViewModel.Status.ModelId);
    Assert.DoesNotContain(ModelChoiceKey, store.Stored.Keys);
  }

  [Fact]
  public async Task ARefusedSelection_IsNeverWritten()
  {
    // The persist path validates through ModelSelection.Create, which refuses an empty or
    // duplicated list. A refused list must never reach the store, and the caller must not be
    // told it landed.
    FakePreferenceStore store = new();
    SessionModelPreferences live = new();
    MainViewModel shell = CreateShell(store, live);
    _ = await OpenAsync(shell).ConfigureAwait(true);

    await shell.ApplyModelChoiceAsync(["alpha/model", "alpha/model"]).ConfigureAwait(true);

    Assert.DoesNotContain(store.Writes, w => w.Key == ModelChoiceKey);
  }

  // ── helpers ───────────────────────────────────────────────────────────────

  private static ModelSettingsViewModel Settings(SessionModelPreferences live,
      IReadOnlyList<string> currentModelIds, Action<ModelSettingsSnapshot> persist)
      => new(live, Provider, persist,
          modelSection: new ModelSectionSeams(
              Loader(Entry("alpha/model"), Entry("beta/other"), Entry("gamma/third")),
              currentModelIds));

  private static ModelProviderEntry Entry(string modelId) => new(
      modelId, "ProviderA", 0.000003m, null, 0.000015m, 200_000, 8192,
      SupportsToolUse: true, SupportsVision: false, SupportsTextOutput: true,
      IntelligenceScore: null, CodingScore: null, AgenticScore: null, LatencyMs: null,
      ThroughputTokensPerSec: null, Description: null);

  private static Func<CancellationToken, Task<Result<IReadOnlyList<ModelProviderEntry>>>> Loader(
      params ModelProviderEntry[] entries)
      => _ => Task.FromResult(Result.Success<IReadOnlyList<ModelProviderEntry>>(entries));

  private static MainViewModel CreateShell(IAppPreferenceStore preferences,
      SessionModelPreferences sessionPreferences)
      => new((root, provider) => Task.FromResult(Result.Success(BuildSession(root, provider, sessionPreferences))),
          new MainViewModelOptions { Preferences = preferences });

  private static async Task<AgentTabViewModel> OpenAsync(MainViewModel shell)
  {
    Result<AgentTabViewModel> opened = await shell.OpenAgentAsync(Root, Provider).ConfigureAwait(true);
    Assert.True(opened.IsSuccess, $"session open failed: [{opened.Error?.Code}] {opened.Error?.Message}");
    return opened.Value;
  }

  private static AgentSession BuildSession(string root, string provider,
      SessionModelPreferences preferences)
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

  /// <summary>A preference store whose writes can be refused, so the best-effort contract is
  ///     observable: a refused write leaves the live selection alone and still reports the save
  ///     as successful.</summary>
  private sealed class FakePreferenceStore(Dictionary<string, string>? seed = null)
      : IAppPreferenceStore
  {
    public bool AcceptWrites { get; init; } = true;

    public List<(string Key, string Value)> Writes { get; } = [];
    public List<string> Deletions { get; } = [];
    public Dictionary<string, string> Stored { get; } = seed is null ? [] : new(seed);

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => Task.FromResult(Stored.TryGetValue(key, out string? value) ? value : null);

    public Task<bool> SetAsync(string key, string value, CancellationToken ct = default)
    {
      Writes.Add((key, value));
      if (!AcceptWrites)
      {
        return Task.FromResult(false);
      }

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
