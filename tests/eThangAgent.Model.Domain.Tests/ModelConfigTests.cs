using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain.Tests;

public class ModelConfigTests
{
  [Fact]
  public void Create_ValidArgs_ReturnsSuccess()
  {
    Result<ModelConfig> result = ModelConfig.Create("gpt-4o", null, 1024, 0.7f);
    Assert.True(result.IsSuccess);
    ModelConfig config = result.Value!;
    Assert.Equal("gpt-4o", config.ModelId);
    Assert.Equal(1024, config.MaxTokens);
    Assert.Equal(0.7f, config.Temperature);
  }

  [Fact]
  public void Create_EmptyModelId_ReturnsFailure()
  {
    Result<ModelConfig> result = ModelConfig.Create("  ", null, 100, 0.5f);
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidModel", result.Error!.Code);
  }

  [Fact]
  public void Create_MaxTokensZero_ReturnsFailure()
  {
    Result<ModelConfig> result = ModelConfig.Create("model", null, 0, 0.5f);
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidModel", result.Error!.Code);
  }

  [Fact]
  public void Create_MaxTokensNegative_ReturnsFailure()
  {
    Result<ModelConfig> result = ModelConfig.Create("model", null, -1, 0.5f);
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidModel", result.Error!.Code);
  }

  [Fact]
  public void Create_TemperatureBelowZero_ReturnsFailure()
  {
    Result<ModelConfig> result = ModelConfig.Create("model", null, 100, -0.1f);
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidModel", result.Error!.Code);
  }

  [Fact]
  public void Create_TemperatureAboveTwo_ReturnsFailure()
  {
    Result<ModelConfig> result = ModelConfig.Create("model", null, 100, 2.1f);
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidModel", result.Error!.Code);
  }

  [Fact]
  public void Create_TemperatureBoundaries_ReturnSuccess()
  {
    Assert.True(ModelConfig.Create("m", null, 100, 0f).IsSuccess);
    Assert.True(ModelConfig.Create("m", null, 100, 2f).IsSuccess);
  }

  [Fact]
  public void Create_WithProvider_ReturnsSuccessAndCarriesProvider()
  {
    Result<ModelConfig> result = ModelConfig.Create("gpt-4o", "OpenAI", 1024, 0.7f);
    Assert.True(result.IsSuccess);
    Assert.Equal("OpenAI", result.Value!.Provider);
  }

  [Fact]
  public void Create_WithNullProvider_ReturnsSuccess()
  {
    Result<ModelConfig> result = ModelConfig.Create("gpt-4o", null, 1024, 0.7f);
    Assert.True(result.IsSuccess);
    Assert.Null(result.Value!.Provider);
  }
}
