using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Issue #124 (2026-10-15 incident): agent.wait returned a partial transcript
///     sentence as if it were the child's settled report. AgentCapabilityProvider's
///     RenderOutcome renders ANY outcome carrying a non-empty report as success - and a
///     FAILED outcome's report carries the failure-detail sentence ("child agent was
///     interrupted by the user before completing."), which the parent read as its
///     child's deliverable. Only a COMPLETED settlement renders as success; a failed
///     outcome renders its failure annotation regardless of any report text.</summary>
public class AgentWaitHonestOutcomeTests
{
  private static AgentRecord Parent() => AgentRecord.Spawned(AgentId.NewId(), null, 0, "prov/parent", null, "root task", DateTimeOffset.UtcNow);

  private sealed class RuntimeOutcome(AgentRunOutcome outcome) : IAgentRuntime
  {
    public Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success(record.Id));

    public Result<bool> Deliver(AgentId id, PendingMessage message) => Result.Success(true);

    public void InterruptSubtree(AgentId rootOfSubtree) { }

    public void Interrupt(AgentId? childId = null) { }

    public Task<Result<AgentRunOutcome>> WhenSettledAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Success(outcome));

    public Task<Result<AgentId>> Resume(AgentId id, string message, CancellationToken ct = default)
        => Task.FromResult(Result.Success(id));
  }

  [Fact]
  public async Task Wait_FailedOutcomeWithNonEmptyReport_RendersFailureAnnotationNotTheReport()
  {
    AgentId id = AgentId.NewId();
    // The exact incident shape: Failed(Interrupted) whose report field carries the
    // failure detail - the parent must read the failure annotation, not the text.
    AgentRunOutcome failed = new(id, AgentStatus.Failed, AgentFailureReason.Interrupted,
        "Now writing the failing tests...", "prov/child", 1);
    AgentCapabilityProvider provider = new(
        new NoopSpawn(), new QueriesStub(), Parent, runtime: new RuntimeOutcome(failed));

    string json = System.Text.Json.JsonSerializer.Serialize(new { id = id.ToString() });
    CapabilityInvocationResult result = await provider.InvokeAsync("wait", json,
        ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError, "a failed settlement must never render as success");
    Assert.Contains("interrupted", result.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("Now writing the failing tests", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Wait_CompletedOutcome_StillRendersReportVerbatim()
  {
    AgentId id = AgentId.NewId();
    AgentRunOutcome completed = new(id, AgentStatus.Completed, null,
        "the real report", "prov/child", 1);
    AgentCapabilityProvider provider = new(
        new NoopSpawn(), new QueriesStub(), Parent, runtime: new RuntimeOutcome(completed));

    string json = System.Text.Json.JsonSerializer.Serialize(new { id = id.ToString() });
    CapabilityInvocationResult result = await provider.InvokeAsync("wait", json,
        ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal("the real report", result.Content);
  }

  private sealed class NoopSpawn : IAgentSpawnCommand
  {
    public Task<Result<AgentId>> Execute(AgentRecord parent, SpawnRequest request, CancellationToken ct = default)
        => Task.FromResult(Result.Success(AgentId.NewId()));
  }

  private sealed class QueriesStub : IAgentQueries
  {
    public Task<Result<AgentRecord>> GetStatus(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<AgentRecord>(new DomainError("NotFound", "unused")));

    public Task<Result<string>> GetResult(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<string>(new DomainError("NotFound", "unused")));

    public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<IReadOnlyList<AgentRecord>>(new DomainError("NotFound", "unused")));
  }
}
