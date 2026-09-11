using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.OpenRouter.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>View-model tests of the Model Settings window (VM logic only, no Avalonia
///     windows): knob persistence and round-trip through the OpenRouter serializer,
///     invalid-knob save blocking, provider applicability (z.ai N/A knobs), the
///     OpenRouter section's visibility rule, and the empty-text-means-unset rule.</summary>
public class ModelSettingsViewModelTests
{
  private static SessionModelPreferences Snapshot() => new();

  [Fact]
  public void Save_PersistsKnobsAndProviderSettings()
  {
    SessionModelPreferences live = Snapshot();
    ModelSettingsViewModel vm = new(live, ProvidersOpenRouter, persist: _ => { })
    {
      EffortChoice = ModelSettingsViewModel.EffortHigh,
    };
    vm.SetKnob(ModelSettingsViewModel.KnobTemperature, "0.3");
    vm.SetKnob(ModelSettingsViewModel.KnobMaxTokens, "1024");
    vm.SetKnob(ModelSettingsViewModel.KnobTopP, "0.9");
    vm.SetKnob(ModelSettingsViewModel.KnobSeed, "42");
    vm.ServerTools.WebSearch = true;
    vm.ServerTools.MaxToolCallsText = "7";

    bool saved = vm.TrySave(out string? error);

    Assert.True(saved, error ?? "no error");
    Assert.Null(error);
    Assert.Equal(ReasoningEffort.High, live.ReasoningEffort);
    Assert.Equal(0.3f, live.Temperature);
    Assert.Equal(1024, live.MaxTokens);
    Assert.Equal(0.9f, live.TopP);
    Assert.Equal(42, live.Seed);
    Assert.NotNull(live.ProviderSettings);
    OpenRouterRequestSettings parsed = OpenRouterRequestSettings.Parse(live.ProviderSettings)!;
    ServerTools expected = new(MaxToolCalls: 7, WebSearch: true);
    Assert.Equal(expected, parsed.ServerTools);
  }

  [Theory]
  [InlineData("abc")]
  [InlineData("1.5")]
  public void InvalidKnobText_BlocksSave(string topP)
  {
    SessionModelPreferences live = Snapshot();
    ModelSettingsViewModel vm = new(live, ProvidersOpenRouter, persist: _ => { });
    vm.SetKnob(ModelSettingsViewModel.KnobTopP, topP);

    bool saved = vm.TrySave(out string? error);

    Assert.False(saved);
    Assert.Null(live.TopP);
    Assert.Null(live.ProviderSettings);
    Assert.Contains(ModelSettingsViewModel.KnobTopP, error, StringComparison.Ordinal);
  }

  [Fact]
  public void NaKnobsDisabledOnZai()
  {
    ModelSettingsViewModel vm = new(Snapshot(), ProvidersZai, persist: _ => { });

    Assert.False(vm.Knobs[ModelSettingsViewModel.KnobTopK].IsEnabled);
    Assert.False(vm.Knobs[ModelSettingsViewModel.KnobRepetitionPenalty].IsEnabled);
    Assert.False(vm.Knobs[ModelSettingsViewModel.KnobMinP].IsEnabled);
    Assert.False(vm.Knobs[ModelSettingsViewModel.KnobTopA].IsEnabled);
    Assert.False(vm.Knobs[ModelSettingsViewModel.KnobSeed].IsEnabled);
    Assert.False(vm.Knobs[ModelSettingsViewModel.KnobVerbosity].IsEnabled);
    Assert.False(vm.Knobs[ModelSettingsViewModel.KnobParallelToolCalls].IsEnabled);
    Assert.True(vm.Knobs[ModelSettingsViewModel.KnobTemperature].IsEnabled);
    Assert.True(vm.Knobs[ModelSettingsViewModel.KnobTopP].IsEnabled);
    Assert.True(vm.Knobs[ModelSettingsViewModel.KnobFrequencyPenalty].IsEnabled);
    Assert.True(vm.Knobs[ModelSettingsViewModel.KnobPresencePenalty].IsEnabled);
    Assert.True(vm.Knobs[ModelSettingsViewModel.KnobMaxTokens].IsEnabled);
  }

  [Fact]
  public void OrSectionHiddenOnNonOpenRouter()
  {
    SessionModelPreferences live = Snapshot();
    ModelSettingsViewModel vm = new(live, ProvidersZai, persist: _ => { });

    Assert.False(vm.ShowOpenRouterSection);

    bool saved = vm.TrySave(out string? _);
    Assert.True(saved);
    Assert.Null(live.ProviderSettings);
  }

  [Fact]
  public void EmptyTextMeansUnset()
  {
    SessionModelPreferences live = Snapshot();
    live.TopP = 0.9f;
    live.Temperature = 0.3f;
    ModelSettingsViewModel vm = new(live, ProvidersOpenRouter, persist: _ => { });
    Assert.Equal("0.9", vm.Knobs[ModelSettingsViewModel.KnobTopP].Text);
    Assert.Equal("0.3", vm.Knobs[ModelSettingsViewModel.KnobTemperature].Text);

    vm.SetKnob(ModelSettingsViewModel.KnobTopP, "");
    vm.SetKnob(ModelSettingsViewModel.KnobTemperature, "");
    bool saved = vm.TrySave(out string? error);

    Assert.True(saved, error ?? "no error");
    Assert.Null(live.TopP);
    Assert.Null(live.Temperature);
  }

  // ---- Fix round 2: live validation feedback (knob edits re-query save availability) ----

  [Fact]
  public void KnobTextChange_RaisesValidationAndCanSaveNotifications()
  {
    ModelSettingsViewModel vm = new(Snapshot(), ProvidersOpenRouter, persist: _ => { });
    Assert.True(vm.CanSave);
    bool validationChanged = false;
    bool canSaveChanged = false;
    vm.PropertyChanged += (_, e) =>
    {
      if (e.PropertyName == nameof(ModelSettingsViewModel.ValidationError))
      {
        validationChanged = true;
      }

      if (e.PropertyName == nameof(ModelSettingsViewModel.CanSave))
      {
        canSaveChanged = true;
      }
    };

    vm.Knobs[ModelSettingsViewModel.KnobTopP].Text = "abc";

    Assert.True(validationChanged, "ValidationError did not re-query on a knob text edit");
    Assert.True(canSaveChanged, "CanSave did not re-query on a knob text edit");
    Assert.False(vm.CanSave);
    Assert.NotNull(vm.ValidationError);

    vm.Knobs[ModelSettingsViewModel.KnobTopP].Text = "0.9";

    Assert.True(vm.CanSave);
    Assert.Null(vm.ValidationError);
  }

  [Fact]
  public void InvalidKnobText_ShowsValidationErrorImmediately()
  {
    ModelSettingsViewModel vm = new(Snapshot(), ProvidersOpenRouter, persist: _ => { });
    Assert.Null(vm.ValidationError);

    vm.Knobs[ModelSettingsViewModel.KnobTemperature].Text = "not-a-number";

    Assert.NotNull(vm.ValidationError);
    Assert.Contains(ModelSettingsViewModel.KnobTemperature, vm.ValidationError, StringComparison.Ordinal);
  }

  [Fact]
  public void KnobTextChange_EnablesAndDisablesSaveCommand()
  {
    ModelSettingsViewModel vm = new(Snapshot(), ProvidersOpenRouter, persist: _ => { });
    Assert.True(vm.SaveCommand.CanExecute(null));

    vm.Knobs[ModelSettingsViewModel.KnobTopP].Text = "5";

    Assert.False(vm.SaveCommand.CanExecute(null));

    vm.Knobs[ModelSettingsViewModel.KnobTopP].Text = "0.5";

    Assert.True(vm.SaveCommand.CanExecute(null));
  }

  [Fact]
  public void SetKnob_MatchesDirectTextEntry()
  {
    ModelSettingsViewModel viaHelper = new(Snapshot(), ProvidersOpenRouter, persist: _ => { });
    ModelSettingsViewModel viaBinding = new(Snapshot(), ProvidersOpenRouter, persist: _ => { });

    viaHelper.SetKnob(ModelSettingsViewModel.KnobTopP, "1.5");
    viaBinding.Knobs[ModelSettingsViewModel.KnobTopP].Text = "1.5";

    Assert.Equal(viaHelper.CanSave, viaBinding.CanSave);
    Assert.Equal(viaHelper.ValidationError, viaBinding.ValidationError);
    Assert.False(viaBinding.CanSave);
  }

  // ---- Fix round 2: non-finite float text (NaN/Infinity) must never save ----

  [Theory]
  [InlineData("NaN")]
  [InlineData("Infinity")]
  [InlineData("-Infinity")]
  public void NonFiniteFloatText_BlocksSave(string text)
  {
    SessionModelPreferences live = Snapshot();
    ModelSettingsViewModel vm = new(live, ProvidersOpenRouter, persist: _ => { });
    vm.SetKnob(ModelSettingsViewModel.KnobTemperature, text);

    bool saved = vm.TrySave(out string? error);

    Assert.False(saved);
    Assert.Null(live.Temperature);
    Assert.Null(live.ProviderSettings);
    Assert.NotNull(error);
    Assert.Contains(KnobTemperatureMapKey, error, StringComparison.Ordinal);
    Assert.False(vm.CanSave);
  }

  [Fact]
  public void InfinityText_RejectedOnEveryFloatKnob()
  {
    foreach (string knob in new[]
    {
      ModelSettingsViewModel.KnobTemperature,
      ModelSettingsViewModel.KnobTopP,
      ModelSettingsViewModel.KnobFrequencyPenalty,
      ModelSettingsViewModel.KnobPresencePenalty,
      ModelSettingsViewModel.KnobRepetitionPenalty,
      ModelSettingsViewModel.KnobMinP,
      ModelSettingsViewModel.KnobTopA,
    })
    {
      SessionModelPreferences live = Snapshot();
      ModelSettingsViewModel vm = new(live, ProvidersOpenRouter, persist: _ => { });
      vm.SetKnob(knob, "Infinity");

      Assert.False(vm.CanSave, $"{knob} accepted Infinity");
      Assert.False(vm.TrySave(out string? _), $"{knob} saved Infinity");
      Assert.Contains(knob, vm.ValidationError, StringComparison.Ordinal);
    }
  }

  // ---- Fix round 2: persisted verbosity round-trip through the shell persistence ----

  [Theory]
  [InlineData(VerbosityLevel.Low)]
  [InlineData(VerbosityLevel.Medium)]
  [InlineData(VerbosityLevel.High)]
  [InlineData(VerbosityLevel.XHigh)]
  [InlineData(VerbosityLevel.Max)]
  public async Task SavedVerbosity_RestoresOnReopen(VerbosityLevel level)
  {
    FakePreferenceStore store = new();
    SessionModelPreferences first = new();
    MainViewModel writer = CreateSettingsShell(store, first);
    _ = await OpenShellAsync(writer, ShellRoot, ProvidersOpenRouter).ConfigureAwait(true);

    SessionModelPreferences live = new() { Verbosity = level };
    ModelSettingsSnapshot? captured = null;
    ModelSettingsViewModel vm = new(live, ProvidersOpenRouter, persist: snapshot => captured = snapshot);
    string wireText = vm.Knobs[ModelSettingsViewModel.KnobVerbosity].Text;
    Assert.False(string.IsNullOrWhiteSpace(wireText), "verbosity knob did not prefill from the typed member");

    bool saved = vm.TrySave(out string? error);
    Assert.True(saved, error ?? "no error");
    ModelSettingsSnapshot savedSnapshot = captured ?? throw new InvalidOperationException("save never invoked the persistence seam");
    await writer.ApplySamplingSettingsAsync(savedSnapshot).ConfigureAwait(true);
    string stored = store.Stored[$"sampling_prefs:{ProvidersOpenRouter}:{ShellRoot}"];
    Assert.Contains(KnobVerbosityKey, stored, StringComparison.Ordinal);

    SessionModelPreferences restored = new();
    MainViewModel reopened = CreateSettingsShell(store, restored);
    _ = await OpenShellAsync(reopened, ShellRoot, ProvidersOpenRouter).ConfigureAwait(true);

    Assert.Equal(level, restored.Verbosity);
  }

  // ---- Fix round 2: an all-unset save deletes the sampling_prefs key ----

  [Fact]
  public async Task AllUnsetSave_DeletesSamplingPrefsKey()
  {
    FakePreferenceStore store = new();
    string samplingKey = $"sampling_prefs:{ProvidersOpenRouter}:{ShellRoot}";
    store.Stored[samplingKey] = "{\"" + KnobTemperatureMapKey + "\":\"0.3\"}";
    SessionModelPreferences first = new();
    SessionModelPreferences live = new();
    MainViewModel shell = CreateSettingsShell(store, first);
    _ = await OpenShellAsync(shell, ShellRoot, ProvidersOpenRouter).ConfigureAwait(true);

    ModelSettingsSnapshot? captured = null;
    ModelSettingsViewModel vm = new(live, ProvidersOpenRouter, persist: snapshot => captured = snapshot);
    bool saved = vm.TrySave(out string? error);
    Assert.True(saved, error ?? "no error");
    Assert.NotNull(captured);
    Assert.All(captured.KnobTexts.Values, value => Assert.Equal(string.Empty, value));
    store.Deletions.Clear();

    ModelSettingsSnapshot savedSnapshot = captured ?? throw new InvalidOperationException("save never invoked the persistence seam");
    await shell.ApplySamplingSettingsAsync(savedSnapshot).ConfigureAwait(true);

    Assert.Contains(samplingKey, store.Deletions);
    Assert.DoesNotContain(store.Writes, w => w.Key == samplingKey);
    Assert.False(store.Stored.ContainsKey(samplingKey), "the all-unset save must not leave a stored map behind");
  }

  private const string KnobTemperatureMapKey = "temperature";
  private const string KnobVerbosityKey = "\"verbosity\"";
  private const string ShellRoot = @"C:\work\model-settings-roundtrip";

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

  private const string ProvidersOpenRouter = "openrouter";
  private const string ProvidersZai = "zai";
}
