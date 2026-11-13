using eThangAgent.ModelDomain;

namespace eThangAgent.Model.Domain.Tests;

/// <summary>How the session's ordered model list reaches the config: the head runs and the rest
///     follow it, in the order the user stated, each carrying the session's routing variant.
///     <para>THE HEAD IS NOT IN THE FALLBACK LIST. The config's own model id is the head, and the
///     request is assembled from that field plus these, so a list that repeated the head would
///     give the provider two places to disagree about which model comes first.</para></summary>
public class ModelListOverlayTests
{
  private const string Head = "alpha/model";
  private const string Second = "beta/other";
  private const string Third = "gamma/third";

  [Fact]
  public void Apply_TheListMinusItsHead_BecomesTheFallbacksInOrder()
  {
    ModelConfig applied = ModelPreferencesOverlay.Apply(
        Config(Head), Prefs([Head, Second, Third]));

    Assert.Equal([Second, Third], applied.FallbackModelIds);
  }

  [Fact]
  public void Apply_AHeadOnlyList_LeavesNoFallbacks()
  {
    ModelConfig applied = ModelPreferencesOverlay.Apply(Config(Head), Prefs([Head]));

    Assert.Empty(applied.FallbackModelIds);
  }

  [Fact]
  public void Apply_APreferenceCarryingNoList_LeavesTheConfigsFallbacksAlone()
  {
    // The resolver sets the list on the config it builds; a preference holding nothing but the
    // variant states nothing about models and must not erase what resolution produced.
    ModelConfig config = Config(Head) with { FallbackModelIds = [Second, Third] };

    ModelConfig applied = ModelPreferencesOverlay.Apply(config,
        new SessionModelPreferences { ModelVariant = ModelVariant.Nitro });

    Assert.Equal([Second, Third], applied.FallbackModelIds);
  }

  [Fact]
  public void Apply_APreferenceCarryingNoList_LeavesAnEmptyListEmpty()
  {
    ModelConfig applied = ModelPreferencesOverlay.Apply(Config(Head), new SessionModelPreferences());

    Assert.Empty(applied.FallbackModelIds);
  }

  [Theory]
  [InlineData(ModelVariant.Standard, "", "")]
  [InlineData(ModelVariant.Nitro, ":nitro", ":nitro")]
  [InlineData(ModelVariant.Floor, ":floor", ":floor")]
  [InlineData(ModelVariant.CacheFloor, "", "")]
  public void Apply_TheVariantReachesEveryFallback_AsItReachesTheHead(
      ModelVariant variant, string headSuffix, string backupSuffix)
  {
    SessionModelPreferences prefs = Prefs([Head, Second, Third]);
    prefs.ModelVariant = variant;

    ModelConfig applied = ModelPreferencesOverlay.Apply(Config(Head), prefs);

    Assert.Equal(Head + headSuffix, applied.ModelId);
    Assert.Equal([Second + backupSuffix, Third + backupSuffix], applied.FallbackModelIds);
  }

  [Fact]
  public void Apply_TheHeadsContextWindowStaysInForce_WhateverTheBackupsWouldOffer()
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
