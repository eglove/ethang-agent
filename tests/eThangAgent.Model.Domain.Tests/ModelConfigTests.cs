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
}
