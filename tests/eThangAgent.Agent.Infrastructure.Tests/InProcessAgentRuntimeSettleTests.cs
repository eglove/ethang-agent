using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;
namespace eThangAgent.AgentInfrastructure.Tests;

/// <summary>WhenSettledAsync contract: pre-start NotFound, completion on settle, waiter
///     cancellation that leaves the TCS (and other waiters) untouched, same-id retry
///     reuse, and failure-path settlement.</summary>
public class InProcessAgentRuntimeSettleTests
{
  private static AgentRecord RunningChild(string prompt = "child task") =>
      AgentRecord.Spawned(new AgentId(Guid.NewGuid()), parentId: null, depth: 1,
          modelUsed: "mock/model", label: "test", taskPrompt: prompt, createdAt: DateTimeOffset.UtcNow);

  private static AgentRunOutcome CompletedOutcome(AgentId childId, string report) =>
      new(childId, AgentStatus.Completed, Reason: null, Report: report, ModelUsed: "mock/model", Depth: 1);

  /// <summary>Runner whose single shared gate holds children in-flight until the test releases them.</summary>
  private sealed class GateRunner : IAgentRunner
  {
    private readonly TaskCompletionSource<AgentRunOutcome> _gate =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<AgentRecord> _firstCall =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<AgentRecord> Started { get; } = [];

    public void Complete(AgentRunOutcome outcome) => _gate.SetResult(outcome);

    public Task<AgentRunOutcome> RunAsync(AgentRecord child, CancellationToken ct = default)
    {
      Started.Add(child);
      _ = _firstCall.TrySetResult(child);
      return _gate.Task;
    }
  }

  /// <summary>Store capturing terminal updates; signals the first one for deterministic awaits.</summary>
  private sealed class FakeStore : IAgentStore
  {
    private readonly TaskCompletionSource<AgentRecord> _firstUpdate =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<AgentRecord> Updates { get; } = [];
    public Task<AgentRecord> FirstUpdate => _firstUpdate.Task.WaitAsync(TimeSpan.FromSeconds(10));

    public Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
    {
      lock (Updates)
      {
        Updates.Add(record);
      }

      _ = _firstUpdate.TrySetResult(record);
      return Task.FromResult(Result.Success("updated"));
    }

    public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
        => throw new NotSupportedException("not exercised by runtime tests");

    public Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
        => throw new NotSupportedException("not exercised by runtime tests");

    public Task<Result<string>> AppendMessageAsync(AgentId id, Message message,
        CancellationToken ct = default) => throw new NotSupportedException("not exercised by runtime tests");

    public Task<Result<string>> ReplaceTranscriptAsync(AgentId id, IReadOnlyList<Message> messages, CancellationToken ct = default)
        => Task.FromResult(Result.Success(id.ToString()));

    public Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(
      AgentId id, CancellationToken ct = default) => throw new NotSupportedException("not exercised by runtime tests");

    public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId,
        CancellationToken ct = default) => throw new NotSupportedException("not exercised by runtime tests");

    public Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
        => throw new NotSupportedException("not exercised by runtime tests");
  }

  [Fact]
  public async Task WhenSettled_UnknownId_FailsNotFound()
  {
    GateRunner runner = new();
    FakeStore store = new();
    InProcessAgentRuntime runtime = new(runner, store, maxConcurrentAgents: 1);

    Result<AgentRunOutcome> outcome = await runtime.WhenSettledAsync(new AgentId(Guid.NewGuid()),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(outcome.IsSuccess);
    Assert.Equal("NotFound", outcome.Error.Code);
  }

  [Fact]
  public async Task WhenSettled_CompletesWithOutcome_WhenRunFinishes()
  {
    GateRunner runner = new();
    FakeStore store = new();
    InProcessAgentRuntime runtime = new(runner, store, maxConcurrentAgents: 1);
    AgentRecord child = RunningChild();
    _ = await runtime.Start(child, ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Task<Result<AgentRunOutcome>> wait = runtime.WhenSettledAsync(child.Id, TestContext.Current.CancellationToken);
    runner.Complete(CompletedOutcome(child.Id, "the report"));
    Result<AgentRunOutcome> outcome = await wait.ConfigureAwait(true);

    Assert.True(outcome.IsSuccess);
    Assert.Equal("the report", outcome.Value.Report);
  }

  [Fact]
  public async Task WhenSettled_AlreadySettled_CompletesImmediately_WithOutcome()
  {
    GateRunner runner = new();
    FakeStore store = new();
    InProcessAgentRuntime runtime = new(runner, store, maxConcurrentAgents: 1);
    AgentRecord child = RunningChild();
    _ = await runtime.Start(child, ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
    runner.Complete(CompletedOutcome(child.Id, "already done"));
    _ = await store.FirstUpdate.ConfigureAwait(true); // the terminal write landed

    Result<AgentRunOutcome> outcome = await runtime.WhenSettledAsync(child.Id,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(outcome.IsSuccess);
    Assert.Equal("already done", outcome.Value.Report);
  }

  [Fact]
  public async Task WhenSettled_WaiterCancellation_ReturnsCancelled_AndLeavesChildUntouched()
  {
    GateRunner runner = new();
    FakeStore store = new();
    InProcessAgentRuntime runtime = new(runner, store, maxConcurrentAgents: 1);
    AgentRecord child = RunningChild();
    _ = await runtime.Start(child, ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    using CancellationTokenSource waiterCts = new();
    Task<Result<AgentRunOutcome>> waiter = runtime.WhenSettledAsync(child.Id, waiterCts.Token);
    await waiterCts.CancelAsync().ConfigureAwait(true);
    Result<AgentRunOutcome> cancelled = await waiter.ConfigureAwait(true);

    Assert.False(cancelled.IsSuccess);
    Assert.Equal("Cancelled", cancelled.Error.Code);
    Assert.Empty(store.Updates); // the child itself was never disturbed

    runner.Complete(CompletedOutcome(child.Id, "still finished"));
    Result<AgentRunOutcome> late = await runtime.WhenSettledAsync(child.Id,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(late.IsSuccess);
    Assert.Equal("still finished", late.Value.Report);
  }

  [Fact]
  public async Task WhenSettled_SameIdRetry_KeepsTheOriginalAwaitAlive()
  {
    GateRunner runner = new();
    FakeStore store = new();
    InProcessAgentRuntime runtime = new(runner, store, maxConcurrentAgents: 2);
    AgentRecord child = RunningChild();
    _ = await runtime.Start(child, ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
    Task<Result<AgentRunOutcome>> original = runtime.WhenSettledAsync(child.Id, TestContext.Current.CancellationToken);

    // watchdog retry: same id, second start; the FIRST run settles (simulating interrupt),
    // the retry replaces the TCS so the original await survives to the retry's outcome.
    _ = await runtime.Start(child, ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
    runner.Complete(CompletedOutcome(child.Id, "retry outcome"));
    Result<AgentRunOutcome> outcome = await original.ConfigureAwait(true);

    Assert.True(outcome.IsSuccess);
    Assert.Equal("retry outcome", outcome.Value.Report);
  }
}
