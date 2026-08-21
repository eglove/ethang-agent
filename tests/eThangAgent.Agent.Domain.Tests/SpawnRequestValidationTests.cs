using eThangAgent.AgentDomain;
using eThangAgent.AgentDomain.Specifications;

namespace eThangAgent.Agent.Domain.Tests;

public class SpawnRequestValidationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NonEmptyTaskPrompt_EmptyOrWhitespace_ViolatesNamingField(string taskPrompt)
    {
        var violation = new NonEmptyTaskPromptSpecification()
            .ViolationFor(new SpawnRequest(taskPrompt));

        Assert.NotNull(violation);
        Assert.Contains("TaskPrompt", violation!.Message);
    }

    [Fact]
    public void NonEmptyTaskPrompt_NonEmpty_IsSatisfied()
    {
        var request = new SpawnRequest("Summarize the design doc");

        Assert.True(new NonEmptyTaskPromptSpecification().IsSatisfiedBy(request));
        Assert.Null(new NonEmptyTaskPromptSpecification().ViolationFor(request));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("anthropic/claude-sonnet-4")]
    public void ValidModelReference_AbsentOrWellFormed_IsSatisfied(string? model)
    {
        var request = new SpawnRequest("Summarize the design doc", Model: model);

        Assert.True(new ValidModelReferenceSpecification().IsSatisfiedBy(request));
        Assert.Null(new ValidModelReferenceSpecification().ViolationFor(request));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidModelReference_Whitespace_ViolatesNamingField(string model)
    {
        var violation = new ValidModelReferenceSpecification()
            .ViolationFor(new SpawnRequest("Summarize the design doc", Model: model));

        Assert.NotNull(violation);
        Assert.Contains("Model", violation!.Message);
    }

    [Fact]
    public void NewId_YieldsDistinctIds()
    {
        var first = AgentId.NewId();
        var second = AgentId.NewId();

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ToString_BareGuidString_RoundTrips()
    {
        var id = AgentId.NewId();

        var parsed = Guid.Parse(id.ToString());

        Assert.Equal(36, id.ToString().Length);
        Assert.Equal(id.Value, parsed);
    }
}
