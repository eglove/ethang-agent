// The raw JSON literal below is deliberately opaque: ProviderSettings is copied
// verbatim and must never be parsed outside the OpenRouter ACL.
#pragma warning disable JSON002
using eThangAgent.ModelDomain;

namespace eThangAgent.Model.Domain.Tests;

public class SessionModelPreferencesOverlayTests
{
  [Fact]
  public void Apply_ReplacesSetKnobs_PreservesUnset()
  {
    ModelConfig config = ModelConfig.Create("m", "openrouter", 512, 0.5f, 8192, topP: 0.9f, seed: 7).Value!;
    SessionModelPreferences prefs = new()
    {
      FrequencyPenalty = 0.5f,
      ParallelToolCalls = false,
    };

    ModelConfig applied = ModelPreferencesOverlay.Apply(config, prefs);

    Assert.Equal(0.9f, applied.TopP);
    Assert.Equal(7, applied.Seed);
    Assert.Equal(0.5f, applied.FrequencyPenalty);
    Assert.False(applied.ParallelToolCalls);
  }

  [Fact]
  public void Apply_CarriesProviderSettingsVerbatim()
  {
    ModelConfig config = ModelConfig.Create("m", "openrouter", 512, 0.5f, 8192).Value!;
    string json = """
        {"reasoning":{"exclude":true},"provider":{"order":["zai"]}}
        """;
    SessionModelPreferences prefs = new() { ProviderSettings = json };

    ModelConfig applied = ModelPreferencesOverlay.Apply(config, prefs);
    Assert.Equal(json, applied.ProviderSettings);

    ModelConfig unchanged = ModelPreferencesOverlay.Apply(config, null);
    Assert.Equal(config, unchanged);
  }

  [Fact]
  public void Apply_DoesNotTouchTemperatureOrEffort()
  {
    ModelConfig config = ModelConfig.Create("m", "openrouter", 512, 0.5f, 8192, ReasoningEffort.High, topK: 40).Value!;
    SessionModelPreferences prefs = new() { ReasoningEffort = ReasoningEffort.Low, TopK = 80 };

    ModelConfig applied = ModelPreferencesOverlay.Apply(config, prefs);

    Assert.Equal(0.5f, applied.Temperature);
    Assert.Equal(ReasoningEffort.High, applied.Effort);
    Assert.Equal(80, applied.TopK);
  }

#pragma warning restore JSON002
}
