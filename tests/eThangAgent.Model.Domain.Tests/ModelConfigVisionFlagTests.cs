using eThangAgent.ModelDomain;

namespace eThangAgent.Model.Domain.Tests;

/// <summary>The vision capability flag on ModelConfig: defaults to false, survives
///     resolution-time construction, and is carried through ModelPreferencesOverlay's
///     knob copy — a sampling-knob overlay must never drop it.</summary>
public class ModelConfigVisionFlagTests
{
  [Fact]
  public void Create_DefaultsAcceptsImageInputToFalse()
  {
    ModelConfig config = ModelConfig.Create("m", "openrouter", 100, 0.5f, 2048).Value!;

    Assert.False(config.AcceptsImageInput);
  }

  [Fact]
  public void Create_ExplicitTrue_IsCarried()
  {
    ModelConfig config = ModelConfig.Create("m", "openrouter", 100, 0.5f, 2048, acceptsImageInput: true).Value!;

    Assert.True(config.AcceptsImageInput);
  }

  [Fact]
  public void Overlay_PreservesAcceptsImageInput()
  {
    ModelConfig config = ModelConfig.Create("m", "openrouter", 512, 0.5f, 8192, acceptsImageInput: true).Value!;
    SessionModelPreferences prefs = new() { Temperature = 0.2f, Seed = 7 };

    ModelConfig applied = ModelPreferencesOverlay.Apply(config, prefs);

    Assert.True(applied.AcceptsImageInput);
    Assert.Equal(0.2f, applied.Temperature);
  }
}
