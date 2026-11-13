using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>The retention toggle in Model Settings, and the badge that reports it.
///     <para>THE DEFAULT IS THE FEATURE: a session that never opens the dialog must still route
///     restricted, and the only way to stop that is an explicit unchecking. These tests pin the
///     default, pin that unchecking travels as a real choice, and pin that the status bar names
///     the promise while it holds.</para></summary>
public class ZeroDataRetentionSettingsTests
{
  private const string ProvidersOpenRouter = "openrouter";

  private static ModelProviderEntry Entry(string modelId) => new(
      modelId, "ProviderA", 0.000003m, null, 0.000015m, 200_000, 8192,
      SupportsToolUse: true, SupportsVision: false, SupportsTextOutput: true,
      IntelligenceScore: null, CodingScore: null, AgenticScore: null, LatencyMs: null,
      ThroughputTokensPerSec: null, Description: null);

  private static Func<CancellationToken, Task<Result<IReadOnlyList<ModelProviderEntry>>>> Loader(
      params ModelProviderEntry[] entries)
      => _ => Task.FromResult(Result.Success<IReadOnlyList<ModelProviderEntry>>(entries));

  private static ModelSettingsViewModel Settings(SessionModelPreferences live,
      Action<ModelSettingsSnapshot> persist, bool currentZeroDataRetention = true)
      => new(live, ProvidersOpenRouter, persist,
          modelSection: new ModelSectionSeams(Loader(Entry("alpha/model")), "alpha/model",
              CurrentZeroDataRetention: currentZeroDataRetention));

  [Fact]
  public async Task TheToggle_StartsOn_ForASessionThatNeverStatedAPolicy()
  {
    ModelSettingsViewModel vm = Settings(new SessionModelPreferences(), _ => { });

    await vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);

    Assert.True(vm.ModelSection!.ZeroDataRetention);
  }

  [Fact]
  public async Task TheToggle_ShowsTheSessionsCurrentPolicy()
  {
    ModelSettingsViewModel vm = Settings(new SessionModelPreferences(), _ => { },
        currentZeroDataRetention: false);

    await vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);

    Assert.False(vm.ModelSection!.ZeroDataRetention);
  }

  [Fact]
  public async Task OpeningAndSaving_WithoutTouchingAnything_KeepsTheRestrictionAndSaysUnchanged()
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
  public async Task UncheckingTheToggle_IsARealChoice_EvenWithTheModelUntouched()
  {
    // The box is an independent axis of the same decision. A user who only unchecks it has
    // made a decision, and reporting Unchanged would discard it silently.
    SessionModelPreferences live = new();
    ModelSettingsSnapshot? received = null;
    ModelSettingsViewModel vm = Settings(live, s => received = s);
    await vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);
    vm.ModelSection!.ZeroDataRetention = false;

    bool saved = vm.TrySave(out string? error);

    Assert.True(saved, error ?? "no error");
    Assert.Equal(ModelChoiceKind.Pinned, received!.Model!.Kind);
    Assert.Equal(RetentionPolicy.RetentionAndTraining, received.Model.Retention);
    Assert.Equal(RetentionPolicy.RetentionAndTraining, live.Retention);
  }
  [Fact]
  public async Task SavingTheLoosenedPolicy_SerializesNoProviderSettings()
  {
    // The policy rides the model choice, not the opaque OpenRouter payload: an all-default
    // provider section still saves null, so turning the restriction off cannot resurrect a
    // stale settings blob.
    ModelSettingsSnapshot? received = null;
    ModelSettingsViewModel vm = Settings(new SessionModelPreferences(), s => received = s);
    await vm.LoadModelCatalogAsync(TestContext.Current.CancellationToken);
    vm.ModelSection!.ZeroDataRetention = false;

    _ = vm.TrySave(out _);

    Assert.Null(received!.ProviderSettingsJson);
  }

  [Fact]
  public void TheStatusBar_NamesTheRestriction_WhileItHolds()
  {
    SessionModelPreferences preferences = new();
    AgentSessionViewModel vm = new(
        (_, _, _, _) => Task.FromResult(Result.Success("")),
        new RecordingLifecycle(new StubStore()), AgentId.NewId(), new Conversation(),
        "OpenRouter", "openrouter/auto",
        new AgentSessionViewModelOptions { WorkspaceRoot = @"C:\work\demo", ModelPreferences = preferences });

    vm.ApplyModelChoice("anthropic/claude", ModelVariant.Standard, zeroDataRetention: true);

    Assert.Equal(AgentSessionViewModel.ZeroDataRetentionBadge, vm.Status.ServingRetention);
  }

  [Fact]
  public void TheStatusBar_SaysNothing_WhenTheRestrictionIsOff()
  {
    // The badge names the promise while it holds, so its ABSENCE is what a loosened session
    // looks like - the same convention the routing variant already follows.
    SessionModelPreferences preferences = new();
    AgentSessionViewModel vm = new(
        (_, _, _, _) => Task.FromResult(Result.Success("")),
        new RecordingLifecycle(new StubStore()), AgentId.NewId(), new Conversation(),
        "OpenRouter", "openrouter/auto",
        new AgentSessionViewModelOptions { WorkspaceRoot = @"C:\work\demo", ModelPreferences = preferences });

    vm.ApplyModelChoice("anthropic/claude", ModelVariant.Standard, zeroDataRetention: false);

    Assert.Equal(string.Empty, vm.Status.ServingRetention);
    Assert.Equal(RetentionPolicy.RetentionAndTraining, preferences.Retention);
  }

  [Fact]
  public void AChoiceThatNamesOnlyAModel_LeavesThePolicyAlone()
  {
    // A caller that states no policy is not loosening one: the session keeps what it had.
    SessionModelPreferences preferences = new() { Retention = RetentionPolicy.RetentionAndTraining };
    AgentSessionViewModel vm = new(
        (_, _, _, _) => Task.FromResult(Result.Success("")),
        new RecordingLifecycle(new StubStore()), AgentId.NewId(), new Conversation(),
        "OpenRouter", "openrouter/auto",
        new AgentSessionViewModelOptions { WorkspaceRoot = @"C:\work\demo", ModelPreferences = preferences });

    vm.ApplyModelChoice("anthropic/claude");

    Assert.Equal(RetentionPolicy.RetentionAndTraining, preferences.Retention);
  }
}
