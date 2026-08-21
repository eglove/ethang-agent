using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

public class AgentQueriesTests
{
    private static AgentRecord MakeRecord(AgentId id, AgentStatus status,
        AgentFailureReason? reason = null, string? finalReport = null) => new(
        id, null, 0, status, reason, "mock/model", null, "task",
        DateTimeOffset.UtcNow, status is AgentStatus.Running ? null : DateTimeOffset.UtcNow, finalReport);

    [Fact]
    public async Task GetStatus_KnownId_ReturnsRecord()
    {
        var store = new FakeAgentStore();
        var id = new AgentId(Guid.NewGuid());
        var record = MakeRecord(id, AgentStatus.Running);
        await store.SaveAsync(record);
        var queries = new AgentQueries(store);

        var result = await queries.GetStatus(id);

        Assert.True(result.IsSuccess);
        Assert.Equal(record, result.Value);
    }

    [Fact]
    public async Task GetStatus_UnknownId_SurfacesStoreFailureVerbatim()
    {
        var store = new FakeAgentStore();
        var queries = new AgentQueries(store);
        var id = new AgentId(Guid.NewGuid());

        var fromStore = await store.GetAsync(id);
        var fromQueries = await queries.GetStatus(id);

        Assert.False(fromStore.IsSuccess);
        Assert.False(fromQueries.IsSuccess);
        Assert.Equal(fromStore.Error, fromQueries.Error);
    }

    [Fact]
    public void RuntimeErrors_NotFound_MatchesStoreNotFoundConvention()
    {
        var id = Guid.NewGuid();

        Assert.StartsWith("Error [NotFound]: ", RuntimeErrors.NotFound(id));
        Assert.Contains(id.ToString(), RuntimeErrors.NotFound(id));
    }

    [Fact]
    public async Task GetResult_RunningAgent_NotCompleteError()
    {
        var store = new FakeAgentStore();
        var id = new AgentId(Guid.NewGuid());
        await store.SaveAsync(MakeRecord(id, AgentStatus.Running));
        var queries = new AgentQueries(store);

        var result = await queries.GetResult(id);

        Assert.False(result.IsSuccess);
        Assert.Equal("NotComplete", result.Error!.Code);
        Assert.Equal(RuntimeErrors.NotComplete(id.Value),
            $"Error [{result.Error.Code}]: {result.Error.Message}");
    }

    [Fact]
    public async Task GetResult_CompletedAgent_ReturnsFinalReportVerbatim()
    {
        var store = new FakeAgentStore();
        var id = new AgentId(Guid.NewGuid());
        await store.SaveAsync(MakeRecord(id, AgentStatus.Completed, finalReport: "the child's report"));
        var queries = new AgentQueries(store);

        var result = await queries.GetResult(id);

        Assert.True(result.IsSuccess);
        Assert.Equal("the child's report", result.Value);
    }

    [Fact]
    public async Task GetResult_CompletedWithoutReport_NotFoundShapedError()
    {
        var store = new FakeAgentStore();
        var id = new AgentId(Guid.NewGuid());
        await store.SaveAsync(MakeRecord(id, AgentStatus.Completed, finalReport: null));
        var queries = new AgentQueries(store);

        var result = await queries.GetResult(id);

        Assert.False(result.IsSuccess);
        Assert.Equal("NotFound", result.Error!.Code);
        Assert.Equal(RuntimeErrors.NotFound(id.Value),
            $"Error [{result.Error.Code}]: {result.Error.Message}");
    }

    [Fact]
    public async Task GetResult_FailedAgentWithPartialReport_ReturnsPartialReport()
    {
        var store = new FakeAgentStore();
        var id = new AgentId(Guid.NewGuid());
        await store.SaveAsync(MakeRecord(id, AgentStatus.Failed, AgentFailureReason.Timeout,
            finalReport: "partial progress before timeout"));
        var queries = new AgentQueries(store);

        var result = await queries.GetResult(id);

        Assert.True(result.IsSuccess);
        Assert.Equal("partial progress before timeout", result.Value);
    }

    [Theory]
    [InlineData(AgentFailureReason.MaxIterations,
        "Error [MaxIterations]: child agent hit the tool-iteration limit without a final report.")]
    [InlineData(AgentFailureReason.Timeout,
        "Error [Timeout]: child agent timed out before completing.")]
    [InlineData(AgentFailureReason.ProviderError,
        "Error [ProviderError]: agent failed without a report.")]
    public async Task GetResult_FailedWithoutReport_RendersReasonSpecificLine(
        AgentFailureReason reason, string expectedLine)
    {
        var store = new FakeAgentStore();
        var id = new AgentId(Guid.NewGuid());
        await store.SaveAsync(MakeRecord(id, AgentStatus.Failed, reason, finalReport: null));
        var queries = new AgentQueries(store);

        var result = await queries.GetResult(id);

        Assert.True(result.IsSuccess);
        Assert.Equal(expectedLine, result.Value);
    }

    [Fact]
    public async Task GetResult_UnknownId_SurfacesStoreNotFound()
    {
        var store = new FakeAgentStore();
        var queries = new AgentQueries(store);
        var id = new AgentId(Guid.NewGuid());

        var result = await queries.GetResult(id);

        Assert.False(result.IsSuccess);
        Assert.Equal("NotFound", result.Error!.Code);
        Assert.Equal($"Agent {id} was not found.", result.Error.Message);
    }
}
