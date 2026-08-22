namespace eThangAgent.AgentDomain.Tests;

/// <summary>Constructor validation of SubAgentOptions budgets.</summary>
public class SubAgentOptionsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MaxConcurrentAgents_BelowOne_IsRejected(int maxConcurrentAgents)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => new SubAgentOptions(DefaultModel: "m/sub",
                MaxConcurrentAgents: maxConcurrentAgents));

        Assert.Equal("MaxConcurrentAgents", error.ParamName);
    }
}
