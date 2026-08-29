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
    FakeAgentStore store = new();
    AgentId id = new(Guid.NewGuid());
    AgentRecord record = MakeRecord(id, AgentStatus.Running);
    _ = await store.SaveAsync(record);
    AgentQueries queries = new(store);

    Result<AgentRecord> result = await queries.GetStatus(id);

    Assert.True(result.IsSuccess);
    Assert.Equal(record, result.Value);
  }

  [Fact]
  public async Task GetStatus_UnknownId_SurfacesStoreFailureVerbatim()
  {
    FakeAgentStore store = new();
    AgentQueries queries = new(store);
    AgentId id = new(Guid.NewGuid());

    Result<AgentRecord> fromStore = await store.GetAsync(id);
    Result<AgentRecord> fromQueries = await queries.GetStatus(id);

    Assert.False(fromStore.IsSuccess);
    Assert.False(fromQueries.IsSuccess);
    Assert.Equal(fromStore.Error, fromQueries.Error);
  }

  [Fact]
  public void RuntimeErrors_NotFound_MatchesStoreNotFoundConvention()
  {
    Guid id = Guid.NewGuid();

    Assert.StartsWith("Error [NotFound]: ", RuntimeErrors.NotFound(id), StringComparison.Ordinal);
    Assert.Contains(id.ToString(), RuntimeErrors.NotFound(id), StringComparison.Ordinal);
  }

  [Fact]
  public async Task GetResult_RunningAgent_NotCompleteError()
  {
    FakeAgentStore store = new();
    AgentId id = new(Guid.NewGuid());
    _ = await store.SaveAsync(MakeRecord(id, AgentStatus.Running));
    AgentQueries queries = new(store);

    Result<string> result = await queries.GetResult(id);

    Assert.False(result.IsSuccess);
    Assert.Equal("NotComplete", result.Error.Code);
    Assert.Equal(RuntimeErrors.NotComplete(id.Value),
        $"Error [{result.Error.Code}]: {result.Error.Message}");
  }

  [Fact]
  public async Task GetResult_CompletedAgent_ReturnsFinalReportVerbatim()
  {
    FakeAgentStore store = new();
    AgentId id = new(Guid.NewGuid());
    _ = await store.SaveAsync(MakeRecord(id, AgentStatus.Completed, finalReport: "the child's report"));
    AgentQueries queries = new(store);

    Result<string> result = await queries.GetResult(id);

    Assert.True(result.IsSuccess);
    Assert.Equal("the child's report", result.Value);
  }

  [Fact]
  public async Task GetResult_CompletedWithoutReport_NotFoundShapedError()
  {
    FakeAgentStore store = new();
    AgentId id = new(Guid.NewGuid());
    _ = await store.SaveAsync(MakeRecord(id, AgentStatus.Completed, finalReport: null));
    AgentQueries queries = new(store);

    Result<string> result = await queries.GetResult(id);

    Assert.False(result.IsSuccess);
    Assert.Equal("NotFound", result.Error.Code);
    Assert.Equal(RuntimeErrors.NotFound(id.Value),
        $"Error [{result.Error.Code}]: {result.Error.Message}");
  }

  [Fact]
  public async Task GetResult_FailedAgentWithPartialReport_ReturnsPartialReport()
  {
    FakeAgentStore store = new();
    AgentId id = new(Guid.NewGuid());
    _ = await store.SaveAsync(MakeRecord(id, AgentStatus.Failed, AgentFailureReason.Timeout,
        finalReport: "partial progress before timeout"));
    AgentQueries queries = new(store);

    Result<string> result = await queries.GetResult(id);

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
    FakeAgentStore store = new();
    AgentId id = new(Guid.NewGuid());
    _ = await store.SaveAsync(MakeRecord(id, AgentStatus.Failed, reason, finalReport: null));
    AgentQueries queries = new(store);

    Result<string> result = await queries.GetResult(id);

    Assert.True(result.IsSuccess);
    Assert.Equal(expectedLine, result.Value);
  }

  [Fact]
  public async Task GetResult_UnknownId_SurfacesStoreNotFound()
  {
    FakeAgentStore store = new();
    AgentQueries queries = new(store);
    AgentId id = new(Guid.NewGuid());

    Result<string> result = await queries.GetResult(id);

    Assert.False(result.IsSuccess);
    Assert.Equal("NotFound", result.Error.Code);
    Assert.Equal($"Agent {id} was not found.", result.Error.Message);
  }
}
