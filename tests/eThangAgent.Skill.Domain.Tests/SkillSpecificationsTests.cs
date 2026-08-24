using eThangAgent.SkillDomain;
using Xunit;

namespace eThangAgent.Skill.Domain.Tests;

public class SkillSpecificationsTests
{
    [Theory]
    [InlineData("brainstorming")]
    [InlineData("a")]
    [InlineData("abc-123")]
    public void ValidNames_Pass(string name) =>
        Assert.Matches(SkillSpecifications.ValidName, name);

    [Theory]
    [InlineData("")]
    [InlineData("Brainstorming")]
    [InlineData("-lead")]
    [InlineData("has space")]
    [InlineData("way-too-long-0123456789012345678901234567890123456789012345678901234567890123")]
    public void InvalidNames_Fail(string name) =>
        Assert.DoesNotMatch(SkillSpecifications.ValidName, name);
}