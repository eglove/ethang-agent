using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>Fan-out/fan-in over the primitives (D12): children spawn through the normal
///     command; the join collects WhenSettled outcomes; failures produce receipts, never
///     silent drops (A3); empty graphs are rejected.</summary>
public class SpawnGraphHandlerTests
{
  private sealed class FakeSpawnCommand(Func<SpawnRequest, Result<AgentId>> reply) : IAgentSpawnCommand
  {
    public Task<Result<AgentId>> Execute(AgentRecord parent, SpawnRequest request, CancellationToken ct = default)
        => Task.FromResult(reply(request));
  }

  private sealed class FakeRuntime(IEnumerable<AgentId> settleIds) : IAgentRuntime
  {
    public Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success(record.Id));

    public Task<Result<AgentRunOutcome>> WhenSettledAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(settleIds.Contains(id)
            ? Result.Success(new AgentRunOutcome(id, AgentStatus.Completed, null, "ok", "m/x", 1))
            : Result.Success(new AgentRunOutcome(id, AgentStatus.Failed, AgentFailureReason.ProviderError, "", "m/x", 1)));

    public Result<bool> Deliver(AgentId id, PendingMessage message)
        => Result.Success(true);

    public void Interrupt(AgentId? childId = null) { }

    public void InterruptSubtree(AgentId rootOfSubtree) { }
  }

  private static AgentRecord Parent()
      => AgentRecord.Spawned(AgentId.NewId(), null, 0, "prov/model", null, "root", DateTimeOffset.UtcNow);

  [Fact]
  public async Task EmptyGraph_IsRejected()
  {
    AgentRecord parent = Parent();
    SpawnGraphHandler handler = new(new FakeSpawnCommand(_ => Result.Success(AgentId.NewId())), new FakeRuntime([]));

    Result<SpawnGraphOutcome> outcome = await handler.ExecuteAsync(parent,
        new SpawnGraphRequest("fanout", [], new JoinPolicy()), TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(outcome.IsSuccess);
    Assert.Contains("at least one child", outcome.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AllMembersComplete_JoinSucceeds()
  {
    AgentRecord parent = Parent();
    AgentId a = AgentId.NewId();
    AgentId b = AgentId.NewId();
    SpawnGraphHandler handler = new(
        new FakeSpawnCommand(request => Result.Success(request.Label == "a" ? a : b)),
        new FakeRuntime([a, b]));

    Result<SpawnGraphOutcome> outcome = await handler.ExecuteAsync(parent,
        new SpawnGraphRequest("fanout",
            [new SpawnRequest("a", Label: "a"), new SpawnRequest("b", Label: "b")],
            new JoinPolicy()), TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(outcome.IsSuccess);
    Assert.True(outcome.Value.AllCompleted);
    Assert.Equal(2, outcome.Value.Receipts.Count);
  }

  [Fact]
  public async Task MemberFailure_JoinFails_WithReceipts()
  {
    AgentRecord parent = Parent();
    AgentId a = AgentId.NewId();
    AgentId b = AgentId.NewId();
    SpawnGraphHandler handler = new(
        new FakeSpawnCommand(request => Result.Success(request.Label == "a" ? a : b)),
        new FakeRuntime([a])); // b settles failed

    Result<SpawnGraphOutcome> outcome = await handler.ExecuteAsync(parent,
        new SpawnGraphRequest("fanout",
            [new SpawnRequest("a", Label: "a"), new SpawnRequest("b", Label: "b")],
            new JoinPolicy()), TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(outcome.IsSuccess);
    Assert.Contains("JoinFailed", outcome.Error.Code, StringComparison.Ordinal);
  }
}
