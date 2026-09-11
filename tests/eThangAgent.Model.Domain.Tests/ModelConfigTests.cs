using System.Text.Json;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Model.Domain.Tests;

public class ModelConfigTests
{
  [Fact]
  public void Create_WithValidParameters_ReturnsSuccess()
  {
    Result<ModelConfig> result = ModelConfig.Create("gpt-4o", null, 1024, 0.7f, 2048);
    Assert.True(result.IsSuccess);
  }

  [Fact]
  public void Create_WithEmptyModelId_ReturnsFailure()
  {
    Result<ModelConfig> result = ModelConfig.Create("  ", null, 100, 0.5f, 2048);
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidModel", result.Error.Code);
  }

  [Fact]
  public void Create_WithZeroMaxTokens_ReturnsFailure()
  {
    Result<ModelConfig> result = ModelConfig.Create("model", null, 0, 0.5f, 2048);
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidModel", result.Error.Code);
  }

  [Fact]
  public void Create_WithNegativeMaxTokens_ReturnsFailure()
  {
    Result<ModelConfig> result = ModelConfig.Create("model", null, -1, 0.5f, 2048);
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidModel", result.Error.Code);
  }

  // ---- Final fix wave: non-finite floats are outside every documented range (AD6) ----

  [Fact]
  public void Create_WithNaNTemperature_ReturnsFailure()
  {
    // Vacuous range checks let NaN through before the fix — the window already
    // blocks NaN text, so this is the domain boundary's own backstop.
    Result<ModelConfig> result = ModelConfig.Create("model", null, 100, float.NaN, 2048);
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidModel", result.Error.Code);
  }

  [Fact]
  public void Create_WithInfiniteTemperature_ReturnsFailure()
  {
    Result<ModelConfig> result = ModelConfig.Create("model", null, 100, float.PositiveInfinity, 2048);
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidModel", result.Error.Code);
  }

  [Theory]
  [InlineData("NaN")]
  [InlineData("Infinity")]
  [InlineData("-Infinity")]
  public void Create_NonFiniteOnEveryFloatKnob_ReturnsFailure(string kind)
  {
    float value = kind switch
    {
      "NaN" => float.NaN,
      "Infinity" => float.PositiveInfinity,
      _ => float.NegativeInfinity,
    };
    Assert.False(ModelConfig.Create("m", null, 100, 0.5f, 2048, topP: value).IsSuccess, kind + " topP");
    Assert.False(ModelConfig.Create("m", null, 100, 0.5f, 2048, frequencyPenalty: value).IsSuccess, kind + " frequencyPenalty");
    Assert.False(ModelConfig.Create("m", null, 100, 0.5f, 2048, presencePenalty: value).IsSuccess, kind + " presencePenalty");
    Assert.False(ModelConfig.Create("m", null, 100, 0.5f, 2048, repetitionPenalty: value).IsSuccess, kind + " repetitionPenalty");
    Assert.False(ModelConfig.Create("m", null, 100, 0.5f, 2048, minP: value).IsSuccess, kind + " minP");
    Assert.False(ModelConfig.Create("m", null, 100, 0.5f, 2048, topA: value).IsSuccess, kind + " topA");
  }

  [Fact]
  public void Create_WithNegativeTemperature_ReturnsFailure()
  {
    Result<ModelConfig> result = ModelConfig.Create("model", null, 100, -0.1f, 2048);
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidModel", result.Error.Code);
  }

  [Fact]
  public void Create_WithTemperatureAboveTwo_ReturnsFailure()
  {
    Result<ModelConfig> result = ModelConfig.Create("model", null, 100, 2.1f, 2048);
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidModel", result.Error.Code);
  }

  [Fact]
  public void Create_TemperatureBoundaries_ReturnSuccess()
  {
    Assert.True(ModelConfig.Create("m", null, 100, 0f, 2048).IsSuccess);
    Assert.True(ModelConfig.Create("m", null, 100, 2f, 2048).IsSuccess);
  }

  [Fact]
  public void Create_NonPositiveContextWindow_Fails()
  {
    Result<ModelConfig> result = ModelConfig.Create("m", null, 100, 0.5f, 0);
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidContextWindow", result.Error.Code);
  }

  [Fact]
  public void Create_PositiveContextWindow_RoundTrips()
  {
    ModelConfig config = ModelConfig.Create("m", null, 100, 0.5f, 200_000).Value!;
    Assert.Equal(200_000, config.ContextWindow);
  }

  [Fact]
  public void Create_WithProvider_ReturnsSuccessAndCarriesProvider()
  {
    Result<ModelConfig> result = ModelConfig.Create("gpt-4o", "OpenAI", 1024, 0.7f, 2048);
    Assert.True(result.IsSuccess);
    Assert.Equal("OpenAI", result.Value.Provider);
  }

  [Fact]
  public void Create_WithNullProvider_ReturnsSuccess()
  {
    Result<ModelConfig> result = ModelConfig.Create("gpt-4o", null, 1024, 0.7f, 2048);
    Assert.True(result.IsSuccess);
    Assert.Null(result.Value.Provider);
  }

  [Fact]
  public void Create_AcceptsNullKnobs()
  {
    Result<ModelConfig> result = ModelConfig.Create("m", null, 512, 0.5f, 8192);
    Assert.True(result.IsSuccess);
    Assert.Null(result.Value.TopP);
    Assert.Null(result.Value.TopK);
    Assert.Null(result.Value.FrequencyPenalty);
    Assert.Null(result.Value.PresencePenalty);
    Assert.Null(result.Value.RepetitionPenalty);
    Assert.Null(result.Value.MinP);
    Assert.Null(result.Value.TopA);
    Assert.Null(result.Value.Seed);
    Assert.Null(result.Value.Verbosity);
    Assert.Null(result.Value.ParallelToolCalls);
    Assert.Null(result.Value.ProviderSettings);
  }

  [Fact]
  public void Create_WithTopPBelowZero_ReturnsFailure()
  {
    Result<ModelConfig> result = ModelConfig.Create("m", null, 100, 0.5f, 2048, topP: -0.1f);
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidModel", result.Error.Code);
  }

  [Fact]
  public void Create_WithTopPAboveOne_ReturnsFailure()
  {
    Result<ModelConfig> result = ModelConfig.Create("m", null, 100, 0.5f, 2048, topP: 1.1f);
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidModel", result.Error.Code);
  }

  [Fact]
  public void Create_WithNegativeTopK_ReturnsFailure()
  {
    Result<ModelConfig> result = ModelConfig.Create("m", null, 100, 0.5f, 2048, topK: -1);
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidModel", result.Error.Code);
  }

  [Fact]
  public void Create_WithFrequencyPenaltyBelowMinusTwo_ReturnsFailure()
  {
    Result<ModelConfig> result = ModelConfig.Create("m", null, 100, 0.5f, 2048, frequencyPenalty: -2.1f);
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidModel", result.Error.Code);
  }

  [Fact]
  public void Create_WithFrequencyPenaltyAboveTwo_ReturnsFailure()
  {
    Result<ModelConfig> result = ModelConfig.Create("m", null, 100, 0.5f, 2048, frequencyPenalty: 2.1f);
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidModel", result.Error.Code);
  }

  [Fact]
  public void Create_WithPresencePenaltyBelowMinusTwo_ReturnsFailure()
  {
    Result<ModelConfig> result = ModelConfig.Create("m", null, 100, 0.5f, 2048, presencePenalty: -2.1f);
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidModel", result.Error.Code);
  }

  [Fact]
  public void Create_WithPresencePenaltyAboveTwo_ReturnsFailure()
  {
    Result<ModelConfig> result = ModelConfig.Create("m", null, 100, 0.5f, 2048, presencePenalty: 2.1f);
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidModel", result.Error.Code);
  }

  [Fact]
  public void Create_WithRepetitionPenaltyBelowZero_ReturnsFailure()
  {
    Result<ModelConfig> result = ModelConfig.Create("m", null, 100, 0.5f, 2048, repetitionPenalty: -0.1f);
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidModel", result.Error.Code);
  }

  [Fact]
  public void Create_WithRepetitionPenaltyAboveTwo_ReturnsFailure()
  {
    Result<ModelConfig> result = ModelConfig.Create("m", null, 100, 0.5f, 2048, repetitionPenalty: 2.1f);
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidModel", result.Error.Code);
  }

  [Fact]
  public void Create_WithMinPBelowZero_ReturnsFailure()
  {
    Result<ModelConfig> result = ModelConfig.Create("m", null, 100, 0.5f, 2048, minP: -0.1f);
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidModel", result.Error.Code);
  }

  [Fact]
  public void Create_WithMinPAboveOne_ReturnsFailure()
  {
    Result<ModelConfig> result = ModelConfig.Create("m", null, 100, 0.5f, 2048, minP: 1.1f);
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidModel", result.Error.Code);
  }

  [Fact]
  public void Create_WithTopABelowZero_ReturnsFailure()
  {
    Result<ModelConfig> result = ModelConfig.Create("m", null, 100, 0.5f, 2048, topA: -0.1f);
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidModel", result.Error.Code);
  }

  [Fact]
  public void Create_WithTopAAboveOne_ReturnsFailure()
  {
    Result<ModelConfig> result = ModelConfig.Create("m", null, 100, 0.5f, 2048, topA: 1.1f);
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidModel", result.Error.Code);
  }

  [Fact]
  public void Create_AcceptsBoundaryValues()
  {
    Assert.True(ModelConfig.Create("m", null, 100, 0.5f, 2048, topP: 0f).IsSuccess);
    Assert.True(ModelConfig.Create("m", null, 100, 0.5f, 2048, topP: 1f).IsSuccess);
    Assert.True(ModelConfig.Create("m", null, 100, 0.5f, 2048, frequencyPenalty: -2f).IsSuccess);
    Assert.True(ModelConfig.Create("m", null, 100, 0.5f, 2048, frequencyPenalty: 2f).IsSuccess);
    Assert.True(ModelConfig.Create("m", null, 100, 0.5f, 2048, presencePenalty: -2f).IsSuccess);
    Assert.True(ModelConfig.Create("m", null, 100, 0.5f, 2048, presencePenalty: 2f).IsSuccess);
    Assert.True(ModelConfig.Create("m", null, 100, 0.5f, 2048, repetitionPenalty: 0f).IsSuccess);
    Assert.True(ModelConfig.Create("m", null, 100, 0.5f, 2048, repetitionPenalty: 2f).IsSuccess);
  }

  [Fact]
  public void Create_FullSurfaceConstruction()
  {
    string settings = JsonSerializer.Serialize(new { k = "v" });
    ModelConfig config = ModelConfig.Create(
        "m", null, 100, 0.5f, 2048,
        topP: 0.9f,
        topK: 40,
        frequencyPenalty: 0.5f,
        presencePenalty: -1f,
        repetitionPenalty: 1.1f,
        minP: 0.05f,
        topA: 0.2f,
        seed: 42,
        verbosity: VerbosityLevel.High,
        parallelToolCalls: false,
        providerSettings: settings).Value!;
    Assert.Equal(0.9f, config.TopP);
    Assert.Equal(40, config.TopK);
    Assert.Equal(0.5f, config.FrequencyPenalty);
    Assert.Equal(-1f, config.PresencePenalty);
    Assert.Equal(1.1f, config.RepetitionPenalty);
    Assert.Equal(0.05f, config.MinP);
    Assert.Equal(0.2f, config.TopA);
    Assert.Equal(42, config.Seed);
    Assert.Equal(VerbosityLevel.High, config.Verbosity);
    Assert.False(config.ParallelToolCalls!.Value);
    Assert.Equal(settings, config.ProviderSettings);
  }
}
