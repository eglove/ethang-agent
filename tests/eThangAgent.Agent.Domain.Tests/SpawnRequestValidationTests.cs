using eThangAgent.AgentDomain;
using eThangAgent.AgentDomain.Specifications;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Domain.Tests;

public class SpawnRequestValidationTests
{
  [Theory]
  [InlineData("")]
  [InlineData("   ")]
  public void NonEmptyTaskPrompt_EmptyOrWhitespace_ViolatesNamingField(string taskPrompt)
  {
    Violation? violation = new NonEmptyTaskPromptSpecification()
        .ViolationFor(new SpawnRequest(taskPrompt));

    Assert.NotNull(violation);
    Assert.Contains("TaskPrompt", violation.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void NonEmptyTaskPrompt_NonEmpty_IsSatisfied()
  {
    SpawnRequest request = new("Summarize the design doc");

    Assert.True(new NonEmptyTaskPromptSpecification().IsSatisfiedBy(request));
    Assert.Null(new NonEmptyTaskPromptSpecification().ViolationFor(request));
  }

  [Theory]
  [InlineData(null)]
  [InlineData("anthropic/claude-sonnet-4")]
  public void ValidModelReference_AbsentOrWellFormed_IsSatisfied(string? model)
  {
    SpawnRequest request = new("Summarize the design doc", Model: model);

    Assert.True(new ValidModelReferenceSpecification().IsSatisfiedBy(request));
    Assert.Null(new ValidModelReferenceSpecification().ViolationFor(request));
  }

  [Theory]
  [InlineData("")]
  [InlineData("   ")]
  public void ValidModelReference_Whitespace_ViolatesNamingField(string model)
  {
    Violation? violation = new ValidModelReferenceSpecification()
        .ViolationFor(new SpawnRequest("Summarize the design doc", Model: model));

    Assert.NotNull(violation);
    Assert.Contains("Model", violation.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void NewId_YieldsDistinctIds()
  {
    AgentId first = AgentId.NewId();
    AgentId second = AgentId.NewId();

    Assert.NotEqual(first, second);
  }

  [Fact]
  public void ToString_BareGuidString_RoundTrips()
  {
    AgentId id = AgentId.NewId();

    Guid parsed = Guid.Parse(id.ToString());

    Assert.Equal(36, id.ToString().Length);
    Assert.Equal(id.Value, parsed);
  }
}
