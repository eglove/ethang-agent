using eThangAgent.ModelDomain;

namespace eThangAgent.Model.Domain.Tests;

public class TokenUsageTests
{
  [Fact]
  public void ModelResponse_DefaultUsage_IsNull()
  {
    ModelResponse response = new("hi", []);
    Assert.Null(response.Usage);
  }

  [Fact]
  public void ModelResponse_WithUsage_CarriesValues()
  {
    ModelResponse response = new("hi", [], FinishReason.Stop, new TokenUsage(120, 30, 40));
    Assert.Equal(120, response.Usage!.Value.InputTokens);
    Assert.Equal(30, response.Usage.Value.OutputTokens);
    Assert.Equal(40, response.Usage.Value.CachedInputTokens);
  }
}
