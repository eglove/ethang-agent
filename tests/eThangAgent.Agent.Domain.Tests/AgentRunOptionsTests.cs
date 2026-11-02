namespace eThangAgent.AgentDomain.Tests;

/// <summary>Constructor validation of AgentRunOptions budgets.</summary>
public class SubAgentOptionsTests
{
  [Theory]
  [InlineData(0)]
  [InlineData(-1)]
  public void MaxConcurrentAgents_BelowOne_IsRejected(int maxConcurrentAgents)
  {
    ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
        () => new AgentRunOptions(DefaultModel: "m/sub",
            MaxConcurrentAgents: maxConcurrentAgents));

    Assert.Equal("MaxConcurrentAgents", error.ParamName);
  }
}
