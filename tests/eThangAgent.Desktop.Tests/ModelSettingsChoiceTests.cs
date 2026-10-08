using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.OpenRouter.ACL;

namespace eThangAgent.Desktop.Tests;

/// <summary>Fixed-vocabulary settings render as dropdowns over their documented
///     values: each vocabulary is exactly the provider-documented set (verified
///     against the official OpenRouter docs - API parameters, provider routing,
///     web-search plugin, chat-completion API reference), the unset row is always
///     selectable, a persisted out-of-vocabulary value survives as a synthetic
///     (custom) row, and picking a row writes the same text the save path has
///     always persisted (text stays the single source of truth).</summary>
public class ModelSettingsChoiceTests
{
  private const string ProvidersOpenRouter = "openrouter";

  // ---- The vocabularies are exactly the documented sets ----

  [Fact]
  public void VerbosityVocabulary_IsExactlyTheDocumentedEnum()
  {
    string[] documented = ["low", "medium", "high", "xhigh", "max"];

    Assert.Equal(documented, SettingChoiceVocabulary.Verbosity.Select(c => c.Value));
    // Every offered value must parse onto the Model Domain's enum: the dropdown
    // can only offer values the save path accepts.
    Assert.All(documented, value => Assert.True(
        Enum.TryParse<VerbosityLevel>(value, ignoreCase: true, out _),
        $"'{value}' does not parse onto VerbosityLevel"));
  }

  [Fact]
  public void ParallelToolCallsVocabulary_IsExactlyTrueAndFalse()
    => Assert.Equal([true, false],
        SettingChoiceVocabulary.ParallelToolCalls.Select(c => bool.Parse(c.Value)));

  [Fact]
  public void SortVocabulary_IsExactlyTheDocumentedSortKeys()
    => Assert.Equal(["price", "throughput", "latency"],
        SettingChoiceVocabulary.Sort.Select(c => c.Value));

  [Fact]
  public void DataCollectionVocabulary_IsExactlyAllowAndDeny()
    => Assert.Equal(["allow", "deny"],
        SettingChoiceVocabulary.DataCollection.Select(c => c.Value));

  [Fact]
  public void RouteVocabulary_IsExactlyTheDeprecatedAliasValues()
    => Assert.Equal(["fallback", "sort"],
        SettingChoiceVocabulary.Route.Select(c => c.Value));

  [Fact]
  public void WebSearchEngineVocabulary_IsExactlyTheDocumentedEngines()
    => Assert.Equal(["native", "exa", "firecrawl", "parallel", "perplexity"],
        SettingChoiceVocabulary.WebSearchEngine.Select(c => c.Value));

  // ---- The unset row and out-of-vocabulary preservation ----

  [Fact]
  public void WithUnset_PrependsTheUnsetRow()
  {
    IReadOnlyList<SettingChoice> rows = SettingChoiceVocabulary.WithUnset(
        SettingChoiceVocabulary.Sort, string.Empty);

    Assert.Equal(SettingChoice.Unset, rows[0]);
    Assert.Equal(string.Empty, rows[0].Value);
    Assert.Equal(4, rows.Count); // unset + the three documented sort keys
  }

  [Fact]
  public void WithUnset_OutOfVocabularyValue_BecomesASyntheticCustomRow()
  {
    IReadOnlyList<SettingChoice> rows = SettingChoiceVocabulary.WithUnset(
        SettingChoiceVocabulary.Sort, "newsort");

    Assert.Equal(5, rows.Count); // unset + three documented + the custom row
    Assert.Equal("newsort", rows[^1].Value);
    Assert.Contains("newsort", rows[^1].Display, StringComparison.Ordinal);
  }

  // ---- Knob rows: fixed-vocabulary knobs carry choices, free-range do not ----

  [Fact]
  public void VerbosityAndParallelToolCalls_CarryChoices_OtherKnobsDoNot()
  {
    ModelSettingsViewModel vm = new(new SessionModelPreferences(), ProvidersOpenRouter, persist: _ => { });

    Assert.NotNull(vm.Knobs[ModelSettingsViewModel.KnobVerbosity].Choices);
    Assert.NotNull(vm.Knobs[ModelSettingsViewModel.KnobParallelToolCalls].Choices);
    Assert.Null(vm.Knobs[ModelSettingsViewModel.KnobTemperature].Choices);
    Assert.Null(vm.Knobs[ModelSettingsViewModel.KnobSeed].Choices);
  }

  [Fact]
  public void KnobChoice_PrefillsFromTheLivePreference()
  {
    SessionModelPreferences live = new() { Verbosity = VerbosityLevel.XHigh, ParallelToolCalls = false };
    ModelSettingsViewModel vm = new(live, ProvidersOpenRouter, persist: _ => { });

    KnobEntry verbosity = vm.Knobs[ModelSettingsViewModel.KnobVerbosity];
    Assert.Equal("xhigh", verbosity.Text);
    Assert.Equal("xhigh", verbosity.SelectedChoice.Value);

    KnobEntry parallel = vm.Knobs[ModelSettingsViewModel.KnobParallelToolCalls];
    Assert.Equal("false", parallel.SelectedChoice.Value);
  }

  [Fact]
  public void PickingAChoice_WritesTheText_AndTheSavePersistsIt()
  {
    SessionModelPreferences live = new();
    ModelSettingsViewModel vm = new(live, ProvidersOpenRouter, persist: _ => { });
    KnobEntry verbosity = vm.Knobs[ModelSettingsViewModel.KnobVerbosity];

    verbosity.SelectedChoice = verbosity.Choices!.First(c => c.Value == "high");
    Assert.Equal("high", verbosity.Text);

    bool saved = vm.TrySave(out string? error);

    Assert.True(saved, error ?? "no error");
    Assert.Equal(VerbosityLevel.High, live.Verbosity);
  }

  [Fact]
  public void PickingTheUnsetRow_ClearsThePreference()
  {
    SessionModelPreferences live = new() { ParallelToolCalls = true };
    ModelSettingsViewModel vm = new(live, ProvidersOpenRouter, persist: _ => { });
    KnobEntry parallel = vm.Knobs[ModelSettingsViewModel.KnobParallelToolCalls];

    parallel.SelectedChoice = SettingChoice.Unset;
    Assert.Equal(string.Empty, parallel.Text);

    bool saved = vm.TrySave(out string? error);

    Assert.True(saved, error ?? "no error");
    Assert.Null(live.ParallelToolCalls);
  }

  [Fact]
  public void OutOfVocabularyPersistedText_GetsACustomRow_AndIsPreserved()
  {
    // A preference persisted by an older build (or a future API value) must be
    // displayed and preserved verbatim - never silently dropped or coerced.
    SessionModelPreferences live = new();
    ModelSettingsViewModel vm = new(live, ProvidersOpenRouter, persist: _ => { });
    vm.SetKnob(ModelSettingsViewModel.KnobVerbosity, "gigantic");

    KnobEntry verbosity = vm.Knobs[ModelSettingsViewModel.KnobVerbosity];
    Assert.Contains(verbosity.Choices!, c => c.Value == "gigantic");
    Assert.Equal("gigantic", verbosity.SelectedChoice.Value);

    // The save path rejects an unknown verbosity: the custom row is display-only
    // and the save blocks with a message naming the knob - the strict-input
    // contract is unchanged by the dropdown.
    bool saved = vm.TrySave(out string? error);

    Assert.False(saved);
    Assert.NotNull(error);
    Assert.Contains(ModelSettingsViewModel.KnobVerbosity, error, StringComparison.Ordinal);
  }

  // ---- Routing dropdowns project onto the same text fields the save reads ----

  [Fact]
  public void RoutingSortDropdown_RoundTripsThroughTheText()
  {
    ModelSettingsViewModel vm = new(new SessionModelPreferences(), ProvidersOpenRouter, persist: _ => { });

    vm.Routing.SelectedSort = vm.Routing.SortChoices.First(c => c.Value == "throughput");
    Assert.Equal("throughput", vm.Routing.SortText);

    Routing routing = vm.Routing.ToRouting();
    Assert.Equal("throughput", routing.Sort);
  }

  [Fact]
  public void RoutingSortUnsetRow_SavesNull()
  {
    ModelSettingsViewModel vm = new(new SessionModelPreferences(), ProvidersOpenRouter, persist: _ => { });

    vm.Routing.SelectedSort = vm.Routing.SortChoices.First(c => c.Value == "price");
    vm.Routing.SelectedSort = SettingChoice.Unset;

    Assert.Equal(string.Empty, vm.Routing.SortText);
    Assert.Null(vm.Routing.ToRouting().Sort);
  }

  [Fact]
  public void RoutingOutOfVocabularyPersistedSort_GetsACustomRow()
  {
    ModelSettingsViewModel vm = new(new SessionModelPreferences(), ProvidersOpenRouter, persist: _ => { });
    vm.Routing.SortText = "exoticsort";

    Assert.Contains(vm.Routing.SortChoices, c => c.Value == "exoticsort");
    Assert.Equal("exoticsort", vm.Routing.SelectedSort.Value);
  }

  [Fact]
  public void RoutingDataCollectionAndRoute_RoundTripThroughTheirTexts()
  {
    ModelSettingsViewModel vm = new(new SessionModelPreferences(), ProvidersOpenRouter, persist: _ => { });

    vm.Routing.SelectedDataCollection = vm.Routing.DataCollectionChoices.First(c => c.Value == "deny");
    vm.Routing.SelectedRoute = vm.Routing.RouteChoices.First(c => c.Value == "fallback");

    Routing routing = vm.Routing.ToRouting();
    Assert.Equal("deny", routing.DataCollection);
    Assert.Equal("fallback", routing.Route);
  }

  // ---- The web plugin's engine dropdown ----

  [Fact]
  public void WebGroundingEngineDropdown_RoundTripsThroughTheText()
  {
    ModelSettingsViewModel vm = new(new SessionModelPreferences(), ProvidersOpenRouter, persist: _ => { });

    vm.Plugins.SelectedEngine = vm.Plugins.EngineChoices.First(c => c.Value == "exa");
    Assert.Equal("exa", vm.Plugins.WebGroundingEngineText);

    Plugins plugins = vm.Plugins.ToPlugins();
    Assert.Equal("exa", plugins.WebGroundingEngine);

    vm.Plugins.SelectedEngine = SettingChoice.Unset;
    Assert.Null(vm.Plugins.ToPlugins().WebGroundingEngine);
  }
}
