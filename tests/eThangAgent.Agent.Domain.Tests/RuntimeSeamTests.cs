using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

public class RuntimeSeamTests
{
    private sealed class FakeRuntime : IAgentRuntime
    {
        public Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
            => Task.FromResult(Result<AgentId>.Success(record.Id));
    }

    private sealed class FakeRunner : IAgentRunner
    {
        public Task<AgentRunOutcome> RunAsync(AgentRecord child, CancellationToken ct = default)
            => Task.FromResult(new AgentRunOutcome(child.Id, AgentStatus.Completed, null, "report",
                child.ModelUsed, child.Depth));
    }

    [Fact]
    public void Error_Constants_AreAnnotated_AndDistinct()
    {
        var id = Guid.NewGuid();
        var errors = new[]
        {
            RuntimeErrors.CapReached,
            RuntimeErrors.NotFound(id),
            RuntimeErrors.NotComplete(id),
        };
        Assert.All(errors, e => Assert.StartsWith("Error [", e));
        Assert.Equal(3, errors.Distinct().Count());
        Assert.Contains(id.ToString(), RuntimeErrors.NotFound(id));
        Assert.Contains(id.ToString(), RuntimeErrors.NotComplete(id));
    }
}
