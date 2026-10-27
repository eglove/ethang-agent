namespace eThangAgent.ModelDomain.Tests;

/// <summary>The routing VARIANT is the session's model-selection decision, and the overlay is
///     where it reaches the config the provider actually sends: the wire id carries the suffix,
///     so a variant that did not overlay would leave the dropdown cosmetic.
///     <para>The retired provider pin is gone from this path by design - the variant is pure
///     routing, so there is no provider to stamp and no vanished-provider failure to raise.
///     </para></summary>
public class SessionModelPreferencesVariantTests
{
  private static ModelConfig Config(string modelId)
      => ModelConfig.Create(modelId, 512, 0.5f, 8192).Value!;

  [Fact]
  public void NoVariantPreference_LeavesTheResolvedModelIdAlone()
  {
    ModelConfig applied = ModelPreferencesOverlay.Apply(Config("alpha/model"), new SessionModelPreferences());

    Assert.Equal("alpha/model", applied.ModelId);
  }

  [Fact]
  public void ANitroPreference_PutsTheSuffixOnTheWireModelId()
  {
    SessionModelPreferences prefs = new()
    {
      ModelId = "alpha/model",
      ModelVariant = ModelVariant.Nitro,
    };

    ModelConfig applied = ModelPreferencesOverlay.Apply(Config("alpha/model"), prefs);

    Assert.Equal("alpha/model:nitro", applied.ModelId);
  }

  [Fact]
  public void AFloorPreference_PutsTheFloorSuffixOnTheWireModelId()
  {
    SessionModelPreferences prefs = new()
    {
      ModelId = "alpha/model",
      ModelVariant = ModelVariant.Floor,
    };

    Assert.Equal("alpha/model:floor", ModelPreferencesOverlay.Apply(Config("alpha/model"), prefs).ModelId);
  }

  [Fact]
  public void SwitchingBackToStandard_RemovesAPreviouslyWrittenSuffix()
  {
    // The resolver hands the overlay a config it built for the SAME preferences, so a stale
    // suffix must never survive a switch back to Standard - that would keep routing nitro for
    // a user who chose the default.
    SessionModelPreferences prefs = new()
    {
      ModelId = "alpha/model:nitro",
      ModelVariant = ModelVariant.Standard,
    };

    Assert.Equal("alpha/model", ModelPreferencesOverlay.Apply(Config("alpha/model:nitro"), prefs).ModelId);
  }

  [Fact]
  public void AChildConfigWithNoModelPreference_StillCarriesTheVariantsRouting()
  {
    // A child inherits the session's variant (it is a property of the conversation, not of the
    // root agent): its config names its OWN model, so the suffix is applied to that model
    // rather than to the root's.
    SessionModelPreferences prefs = new()
    {
      ModelId = "root/model",
      ModelVariant = ModelVariant.Nitro,
    };

    ModelConfig child = ModelPreferencesOverlay.Apply(Config("child/model"), prefs);

    Assert.Equal("child/model:nitro", child.ModelId);
  }

  [Fact]
  public void AVariantWithoutALiveModelPreference_StillApplies_OnThePinPath()
  {
    // THE SESSION-PIN PATH: the resolver has a model (the persisted record) and the preference
    // carries only the variant. Gating on the preference's model would silently drop the tradeoff
    // on every resumed session - the suffix would vanish from the wire while the status line
    // still named it. The overlay applies to the CONFIG's model, never invents one.
    SessionModelPreferences prefs = new() { ModelVariant = ModelVariant.Nitro };

    Assert.Equal("alpha/model:nitro", ModelPreferencesOverlay.Apply(Config("alpha/model"), prefs).ModelId);
  }

  [Fact]
  public void TheWireModelIdNeverCarriesTwoSuffixes()
  {
    SessionModelPreferences prefs = new()
    {
      ModelId = "alpha/model:floor",
      ModelVariant = ModelVariant.Nitro,
    };

    Assert.Equal("alpha/model:nitro", ModelPreferencesOverlay.Apply(Config("alpha/model:floor"), prefs).ModelId);
  }
}
