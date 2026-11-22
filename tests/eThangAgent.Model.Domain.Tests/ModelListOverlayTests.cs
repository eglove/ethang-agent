using eThangAgent.ModelDomain;

namespace eThangAgent.Model.Domain.Tests;

/// <summary>What the session's preferences do to the config a turn is about to send: the model id
///     stays the one the turn resolved, the routing variant reaches it, and the preference's
///     ordered list changes nothing else.
///     <para>THE PREFERENCE'S LIST IS NOT COPIED ONTO THE CONFIG. A turn walks that list one model
///     at a time, so a config carrying a list of its own would let a provider fall through inside
///     a single request behind the walk's back.</para></summary>
public class ModelListOverlayTests
{
  private const string Head = "alpha/model";
  private const string Second = "beta/other";
  private const string Third = "gamma/third";

  [Fact]
  public void Apply_ALongerPreferenceList_LeavesTheResolvedModelAsTheOnlyModel()
  {
    ModelConfig applied = ModelPreferencesOverlay.Apply(
        Config(Head), Prefs([Head, Second, Third]));

    Assert.Equal(Head, applied.ModelId);
  }

  [Fact]
  public void Apply_AHeadOnlyList_LeavesTheResolvedModelAlone()
  {
    ModelConfig applied = ModelPreferencesOverlay.Apply(Config(Head), Prefs([Head]));

    Assert.Equal(Head, applied.ModelId);
  }

  [Fact]
  public void Apply_APreferenceCarryingNoList_LeavesTheConfigUntouched()
  {
    // A preference holding nothing but the variant states nothing about models and must not
    // rewrite the model resolution produced.
    ModelConfig config = Config(Head);

    ModelConfig applied = ModelPreferencesOverlay.Apply(config,
        new SessionModelPreferences { ModelVariant = ModelVariant.Nitro });

    Assert.Equal(ModelVariants.ApplyTo(ModelVariant.Nitro, Head), applied.ModelId);
  }

  [Theory]
  [InlineData(ModelVariant.Standard, "")]
  [InlineData(ModelVariant.Nitro, ":nitro")]
  [InlineData(ModelVariant.Floor, ":floor")]
  [InlineData(ModelVariant.CacheFloor, "")]
  public void Apply_TheVariantReachesTheModel_WhateverTheListHolds(ModelVariant variant, string suffix)
  {
    SessionModelPreferences prefs = Prefs([Head, Second, Third]);
    prefs.ModelVariant = variant;

    ModelConfig applied = ModelPreferencesOverlay.Apply(Config(Head), prefs);

    Assert.Equal(Head + suffix, applied.ModelId);
    Assert.Equal(variant, applied.Variant);
  }

  [Fact]
  public void Apply_TheModelsContextWindowStaysInForce_WhateverTheListHolds()
  {
    // The window is what the context accountant budgets the turn against, and only one model
    // runs the turn: the list cannot widen or narrow it.
    ModelConfig applied = ModelPreferencesOverlay.Apply(
        Config(Head) with { ContextWindow = 1_000_000 }, Prefs([Head, Second, Third]));

    Assert.Equal(1_000_000, applied.ContextWindow);
  }

  private static SessionModelPreferences Prefs(IReadOnlyList<string> modelIds)
      => new() { ModelIds = modelIds };

  private static ModelConfig Config(string modelId)
      => ModelConfig.Create(modelId, 512, 0.5f, 8192,
          retention: RetentionPolicy.ZeroDataRetention).Value!;
}
