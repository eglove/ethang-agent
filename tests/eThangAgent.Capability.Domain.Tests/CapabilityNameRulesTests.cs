using eThangAgent.CapabilityDomain;

namespace eThangAgent.Capability.Domain.Tests;

public class CapabilityNameRulesTests
{
    [Theory]
    [InlineData("read")]
    [InlineData("Get_Item")]
    [InlineData("a1")]
    public void ValidActionNames_Accepted(string name)
        => Assert.True(CapabilityNameRules.IsValidActionName(name));

    [Theory]
    [InlineData("")]
    [InlineData("read-file")]
    [InlineData("read.file")]
    [InlineData("has space")]
    [InlineData("héllo")]
    public void InvalidActionNames_Rejected(string name)
        => Assert.False(CapabilityNameRules.IsValidActionName(name));
}
