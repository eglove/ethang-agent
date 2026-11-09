namespace eThangAgent.ModelDomain.Tests;

/// <summary>The retention restriction as a preference: ON unless something explicitly turns it
///     off.
///     <para>THE NULL CASE IS THE WHOLE FEATURE. Every other knob in the overlay treats null as
///     'keep what resolution produced', and a session that never opened Model Settings has a
///     null here. If that null meant 'off', the default would be whatever the config happened
///     to carry and the promise would depend on a member nobody set - so the overlay decides,
///     and it decides on.</para></summary>
public class ZeroDataRetentionOverlayTests
{
  private static ModelConfig Config(bool zeroDataRetention = false)
      => ModelConfig.Create("alpha/model", 64, 0.7f, 4096, zeroDataRetention: zeroDataRetention).Value!;

  [Fact]
  public void AnUnstatedPreference_RestrictsRouting()
      // The session has never stated a policy: silence must not be the thing that sends a
      // prompt to a provider that keeps it.
      => Assert.True(ModelPreferencesOverlay.Apply(Config(), new SessionModelPreferences())
          .ZeroDataRetention);
  [Fact]
  public void AnExplicitTrue_RestrictsRouting()
      => Assert.True(ModelPreferencesOverlay.Apply(Config(),
          new SessionModelPreferences { ZeroDataRetention = true }).ZeroDataRetention);

  [Fact]
  public void AnExplicitFalse_IsTheOnlyThingThatLoosensIt()
      => Assert.False(ModelPreferencesOverlay.Apply(Config(zeroDataRetention: true),
          new SessionModelPreferences { ZeroDataRetention = false }).ZeroDataRetention);

  [Fact]
  public void NoPreferencesAtAll_LeavesTheResolvedConfigAlone()
      // A null preference set is 'nothing was overlaid', not 'the user loosened it'. The
      // resolver supplies the default on the path that builds the config.
      => Assert.False(ModelPreferencesOverlay.Apply(Config(), null).ZeroDataRetention);

  [Fact]
  public void ThePolicy_TravelsWithThePreferencesAcrossTheProcessBoundary()
  {
    // The app owns the picker and the agent lives in its own host, so the policy must survive
    // the carrier or a loosened session would silently tighten on every turn.
    SessionModelPreferences original = new() { ZeroDataRetention = false };

    SessionModelPreferences restored =
        SessionModelPreferencesJson.Deserialize(SessionModelPreferencesJson.Serialize(original)).Value!;

    Assert.False(restored.ZeroDataRetention);
  }

  [Fact]
  public void CopyFrom_CarriesThePolicy()
  {
    SessionModelPreferences target = new();

    target.CopyFrom(new SessionModelPreferences { ZeroDataRetention = false });

    Assert.False(target.ZeroDataRetention);
  }
}
