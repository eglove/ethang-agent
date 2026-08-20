namespace eThangAgent.ModelDomain.Tests;

public class ModelConfigTests
{
    [Fact]
    public void Create_ValidArgs_ReturnsSuccess()
    {
        var result = ModelConfig.Create("gpt-4o", 1024, 0.7f);
        Assert.True(result.IsSuccess);
        var config = result.Value!;
        Assert.Equal("gpt-4o", config.ModelId);
        Assert.Equal(1024, config.MaxTokens);
        Assert.Equal(0.7f, config.Temperature);
    }

    [Fact]
    public void Create_EmptyModelId_ReturnsFailure()
    {
        var result = ModelConfig.Create("  ", 100, 0.5f);
        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidModel", result.Error!.Code);
    }

    [Fact]
    public void Create_MaxTokensZero_ReturnsFailure()
    {
        var result = ModelConfig.Create("model", 0, 0.5f);
        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidModel", result.Error!.Code);
    }

    [Fact]
    public void Create_MaxTokensNegative_ReturnsFailure()
    {
        var result = ModelConfig.Create("model", -1, 0.5f);
        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidModel", result.Error!.Code);
    }

    [Fact]
    public void Create_TemperatureBelowZero_ReturnsFailure()
    {
        var result = ModelConfig.Create("model", 100, -0.1f);
        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidModel", result.Error!.Code);
    }

    [Fact]
    public void Create_TemperatureAboveTwo_ReturnsFailure()
    {
        var result = ModelConfig.Create("model", 100, 2.1f);
        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidModel", result.Error!.Code);
    }

    [Fact]
    public void Create_TemperatureBoundaries_ReturnSuccess()
    {
        Assert.True(ModelConfig.Create("m", 100, 0f).IsSuccess);
        Assert.True(ModelConfig.Create("m", 100, 2f).IsSuccess);
    }
}
