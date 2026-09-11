using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.OpenRouter.ACL;

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

  private const string ProvidersOpenRouter = "openrouter";
  private const string ProvidersZai = "zai";
}
