namespace eThangAgent.ModelDomain.Tests;

/// <summary>The retention tier as a preference: the resolved config's tier unless the session
///     states another.
///     <para>NULL KEEPS THE CONFIG'S TIER. Every other knob in the overlay treats null as
///     'keep what resolution produced', and a session that never opened Model Settings has a
///     null here. Defaulting that null to the strict tier would silently tighten a config
///     nobody asked to tighten, so the default is supplied by the config's own parameter
///     default and the overlay only ever replaces a stated tier.</para></summary>
public class ZeroDataRetentionOverlayTests
{
  private static ModelConfig Config(RetentionPolicy retention = RetentionPolicy.RetentionAndTraining)
      => ModelConfig.Create("alpha/model", 64, 0.7f, 4096, retention: retention).Value!;

  [Fact]
  public void AnUnstatedPreference_KeepsTheConfigTier()
      // The session has never stated a policy: silence must not be the thing that changes
      // the tier resolution produced.
      => Assert.Equal(RetentionPolicy.RetentionAndTraining,
          ModelPreferencesOverlay.Apply(Config(), new SessionModelPreferences()).Retention);

  [Fact]
  public void AResolvedStrictTier_SurvivesAnEmptyPreference()
      => Assert.Equal(RetentionPolicy.ZeroDataRetention,
          ModelPreferencesOverlay.Apply(Config(RetentionPolicy.ZeroDataRetention),
              new SessionModelPreferences()).Retention);

  [Fact]
  public void AStatedTier_ReplacesTheConfigTier()
      => Assert.Equal(RetentionPolicy.SafetyRetention,
          ModelPreferencesOverlay.Apply(Config(RetentionPolicy.ZeroDataRetention),
              new SessionModelPreferences { Retention = RetentionPolicy.SafetyRetention }).Retention);

  [Fact]
  public void AStatedLooseningTier_ReplacesAStrictConfig()
      => Assert.Equal(RetentionPolicy.RetentionAndTraining,
          ModelPreferencesOverlay.Apply(Config(RetentionPolicy.ZeroDataRetention),
              new SessionModelPreferences { Retention = RetentionPolicy.RetentionAndTraining }).Retention);

  [Fact]
  public void NoPreferencesAtAll_LeavesTheResolvedConfigAlone()
      // A null preference set is 'nothing was overlaid', not 'the user loosened it'. The
      // resolver supplies the default on the path that builds the config.
      => Assert.Equal(RetentionPolicy.RetentionAndTraining,
          ModelPreferencesOverlay.Apply(Config(), null).Retention);

  [Fact]
  public void TheTier_TravelsWithThePreferencesAcrossTheProcessBoundary()
  {
    // The app owns the picker and the agent lives in its own host, so the tier must survive
    // the carrier or a loosened session would silently tighten on every turn.
    SessionModelPreferences original = new() { Retention = RetentionPolicy.SafetyRetention };

    SessionModelPreferences restored =
        SessionModelPreferencesJson.Deserialize(SessionModelPreferencesJson.Serialize(original)).Value!;

    Assert.Equal(RetentionPolicy.SafetyRetention, restored.Retention);
  }

  [Fact]
  public void CopyFrom_CarriesTheTier()
  {
    SessionModelPreferences target = new();

    target.CopyFrom(new SessionModelPreferences { Retention = RetentionPolicy.RetentionAndTraining });

    Assert.Equal(RetentionPolicy.RetentionAndTraining, target.Retention);
  }
}
