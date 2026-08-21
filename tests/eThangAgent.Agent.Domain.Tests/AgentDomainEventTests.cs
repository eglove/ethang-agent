using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

public class AgentDomainEventTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UtcNow;

    [Fact]
    public void AgentSpawned_CarriesPayload()
    {
        var id = AgentId.NewId();
        var spawned = new AgentSpawned(id, At, 2, "prov/model", "research");

        Assert.Equal(id, spawned.AgentId);
        Assert.Equal(At, spawned.OccurredAt);
        Assert.Equal(2, spawned.Depth);
        Assert.Equal("prov/model", spawned.ModelUsed);
        Assert.Equal("research", spawned.Label);
        Assert.IsAssignableFrom<AgentDomainEvent>(spawned);
    }

    [Fact]
    public void AgentCompleted_CarriesPayload()
    {
        var id = AgentId.NewId();
        var completed = new AgentCompleted(id, At, AgentStatus.Failed, AgentFailureReason.Timeout);

        Assert.Equal(id, completed.AgentId);
        Assert.Equal(At, completed.OccurredAt);
        Assert.Equal(AgentStatus.Failed, completed.Status);
        Assert.Equal(AgentFailureReason.Timeout, completed.Reason);
    }
}