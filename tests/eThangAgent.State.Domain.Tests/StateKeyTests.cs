using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;

namespace eThangAgent.State.Domain.Tests;

public class StateKeyTests
{
  [Fact]
  public void Parse_ValidKey_SplitsSegments()
  {
    Result<(string Ns, string Name)> result = StateKey.Parse("current/head");
    Assert.True(result.IsSuccess);
    Assert.Equal(("current", "head"), result.Value!);
  }

  [Theory]
  [InlineData("")]
  [InlineData("noslash")]
  [InlineData("a/b/c")]
  [InlineData("/head")]
  [InlineData("current/")]
  [InlineData("current /head")]
  public void Parse_InvalidKey_Fails_InvalidKey(string key)
  {
    Result<(string Ns, string Name)> result = StateKey.Parse(key);
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidKey", result.Error!.Code);
  }
}
