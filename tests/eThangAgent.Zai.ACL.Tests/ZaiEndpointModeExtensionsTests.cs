namespace eThangAgent.Zai.ACL.Tests;

/// <summary>The configuration-string contract of <see cref="ZaiEndpointMode"/>: the exact
///     tokens persisted everywhere (env variable, Desktop preference) and the strict
///     parse that rejects anything else.</summary>
public class ZaiEndpointModeExtensionsTests
{
  [Fact]
  public void ToConfigValue_Produces_The_Documented_Tokens()
  {
    Assert.Equal("coding", ZaiEndpointMode.CodingPlan.ToConfigValue());
    Assert.Equal("general", ZaiEndpointMode.GeneralApi.ToConfigValue());
  }

  [Fact]
  public void TryParseConfigValue_Accepts_Only_Exact_Tokens()
  {
    Assert.True("coding".TryParseConfigValue(out ZaiEndpointMode coding));
    Assert.Equal(ZaiEndpointMode.CodingPlan, coding);

    Assert.True("general".TryParseConfigValue(out ZaiEndpointMode general));
    Assert.Equal(ZaiEndpointMode.GeneralApi, general);
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("Coding")]
  [InlineData("GENERAL")]
  [InlineData("coding ")]
  [InlineData("subscription")]
  public void TryParseConfigValue_Rejects_Everything_Else(string? value)
  {
    Assert.False(value.TryParseConfigValue(out ZaiEndpointMode mode));
    Assert.Equal(ZaiEndpointMode.CodingPlan, mode); // out default, not a parsed result
  }
}
