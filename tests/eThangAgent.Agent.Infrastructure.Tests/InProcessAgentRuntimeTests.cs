using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

using eThangAgent.ConversationDomain;
namespace eThangAgent.AgentInfrastructure.Tests;

/// <summary>Unit tests for the in-process actor runtime: immediate start, terminal persistence,
/// provider-error fallback, concurrency cap, and slot recycling. Fakes only.</summary>
public class InProcessAgentRuntimeTests
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

    /// <summary>Resolves once any child actually entered RunAsync (background dispatch observed).</summary>
    public Task FirstCall => _firstCall.Task.WaitAsync(TimeSpan.FromSeconds(10));

    public void Complete(AgentRunOutcome outcome) => _gate.SetResult(outcome);

    public void Throw(Exception exception) => _gate.SetException(exception);

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

    /// <summary>Completes when the first update lands; times out loudly instead of hanging.</summary>
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

    public Task<Result<string>> AppendMessageAsync(AgentId id, ConversationDomain.Message message,
        CancellationToken ct = default) => throw new NotSupportedException("not exercised by runtime tests");

    public Task<Result<string>> ReplaceTranscriptAsync(AgentId id, IReadOnlyList<Message> messages, CancellationToken ct = default)
          => Task.FromResult(Result.Success(id.ToString()));

    public Task<Result<IReadOnlyList<ConversationDomain.Message>>> GetTranscriptAsync(
      AgentId id, CancellationToken ct = default) => throw new NotSupportedException("not exercised by runtime tests");

    public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId,
        CancellationToken ct = default) => throw new NotSupportedException("not exercised by runtime tests");

    public Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
        => throw new NotSupportedException("not exercised by runtime tests");
  }

  [Fact]
  public void Constructor_NonPositiveCap_Throws()
  {
    GateRunner runner = new();
    FakeStore store = new();

    _ = Assert.Throws<ArgumentOutOfRangeException>(() => new InProcessAgentRuntime(runner, store, 0));
    _ = Assert.Throws<ArgumentOutOfRangeException>(() => new InProcessAgentRuntime(runner, store, -3));
  }

  [Fact]
  public async Task Start_ChildRunning_ReturnsOkImmediately_WithNoStoreUpdate()
  {
    GateRunner runner = new();
    FakeStore store = new();
    InProcessAgentRuntime runtime = new(runner, store, maxConcurrentAgents: 2);
    AgentRecord child = RunningChild();

    Result<AgentId> result = await runtime.Start(child, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(child.Id, result.Value);
    await runner.FirstCall.ConfigureAwait(true); // dispatch happened, child still in-flight behind the gate
    Assert.Empty(store.Updates);
  }

  /// <summary>Runner that parks until its token fires, then reports Failed(Interrupted) —
  /// the same shape SubAgentSpawner produces when the runtime cancels a live child.</summary>
  private sealed class CancellingRunner : IAgentRunner
  {
    private readonly TaskCompletionSource<AgentRecord> _firstCall =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CancellationToken ObservedToken { get; private set; }
    public Task FirstCall => _firstCall.Task.WaitAsync(TimeSpan.FromSeconds(10), ObservedToken);

    public async Task<AgentRunOutcome> RunAsync(AgentRecord child, CancellationToken ct = default)
    {
      ObservedToken = ct;
      _ = _firstCall.TrySetResult(child);
      try
      {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        // bounded: the token is the only thing that settles this await
      }
      return new AgentRunOutcome(child.Id, AgentStatus.Failed,
          AgentFailureReason.Interrupted, "child agent was interrupted.", child.ModelUsed, child.Depth);
    }
  }

  [Fact]
  public async Task Interrupt_All_CancelsActiveRunToken_AndPersistsItsTerminalOutcome()
  {
    CancellingRunner runner = new();
    FakeStore store = new();
    InProcessAgentRuntime runtime = new(runner, store, maxConcurrentAgents: 1);
    AgentRecord child = RunningChild();

    _ = await runtime.Start(child, ct: TestContext.Current.CancellationToken);
    await runner.FirstCall.ConfigureAwait(true); // run is in-flight and parked on its token

    runtime.Interrupt(); // stop everything in this session's runtime

    AgentRecord updated = await store.FirstUpdate.ConfigureAwait(true);
    Assert.True(runner.ObservedToken.IsCancellationRequested);
    Assert.Equal(AgentStatus.Failed, updated.Status);
    Assert.Equal(AgentFailureReason.Interrupted, updated.FailureReason);
  }

  [Fact]
  public async Task Interrupt_UnknownId_IsANoOp_AndDoesNotDisturbActiveRuns()
  {
    GateRunner runner = new();
    FakeStore store = new();
    InProcessAgentRuntime runtime = new(runner, store, maxConcurrentAgents: 1);
    AgentRecord child = RunningChild();

    _ = await runtime.Start(child, ct: TestContext.Current.CancellationToken);
    await runner.FirstCall.ConfigureAwait(true);

    runtime.Interrupt(new AgentId(Guid.NewGuid()));

    Assert.Empty(store.Updates); // active run untouched, still parked behind the gate
    runner.Complete(CompletedOutcome(child.Id, "still finished fine"));
    AgentRecord updated = await store.FirstUpdate.ConfigureAwait(true);
    Assert.Equal(AgentStatus.Completed, updated.Status);
  }

  [Fact]
  public async Task RunnerCompletes_Lands_CompletedUpdate_CarryingReport()
  {
    GateRunner runner = new();
    FakeStore store = new();
    InProcessAgentRuntime runtime = new(runner, store, maxConcurrentAgents: 1);
    AgentRecord child = RunningChild();

    _ = await runtime.Start(child, ct: TestContext.Current.CancellationToken);
    runner.Complete(CompletedOutcome(child.Id, "the child report"));

    AgentRecord updated = await store.FirstUpdate.ConfigureAwait(true);

    Assert.Equal(child.Id, updated.Id);
    Assert.Equal(AgentStatus.Completed, updated.Status);
    Assert.Null(updated.FailureReason);
    Assert.Equal("the child report", updated.FinalReport);
    _ = Assert.NotNull(updated.CompletedAt);
  }

  [Fact]
  public async Task RunnerThrows_Lands_FailedProviderError_Update()
  {
    GateRunner runner = new();
    FakeStore store = new();
    InProcessAgentRuntime runtime = new(runner, store, maxConcurrentAgents: 1);
    AgentRecord child = RunningChild();

    _ = await runtime.Start(child, ct: TestContext.Current.CancellationToken);
    runner.Throw(new InvalidOperationException("provider exploded"));

    AgentRecord updated = await store.FirstUpdate.ConfigureAwait(true);

    Assert.Equal(child.Id, updated.Id);
    Assert.Equal(AgentStatus.Failed, updated.Status);
    Assert.Equal(AgentFailureReason.ProviderError, updated.FailureReason);
    Assert.Equal("Error [ProviderError]: provider exploded", updated.FinalReport);
    _ = Assert.NotNull(updated.CompletedAt);
  }

  [Fact]
  public async Task AtCapacity_StartFails_CapReached_WithoutSideEffects()
  {
    GateRunner runner = new();
    FakeStore store = new();
    InProcessAgentRuntime runtime = new(runner, store, maxConcurrentAgents: 1);
    AgentRecord first = RunningChild();

    Result<AgentId> accepted = await runtime.Start(first, ct: TestContext.Current.CancellationToken);
    Assert.True(accepted.IsSuccess);
    await runner.FirstCall.ConfigureAwait(true); // the sole slot's child is provably in-flight

    Result<AgentId> rejected = await runtime.Start(RunningChild(), ct: TestContext.Current.CancellationToken);

    Assert.False(rejected.IsSuccess);
    Assert.NotNull(rejected.Error);
    Assert.Equal(RuntimeErrors.CapReached,
        $"Error [{rejected.Error.Code}]: {rejected.Error.Message}");
    _ = Assert.Single(runner.Started); // rejected request never reached the runner
    Assert.Empty(store.Updates); // nothing persisted by either start yet
  }

  [Fact]
  public async Task ReleasedSlot_NextStart_Succeeds()
  {
    GateRunner runner = new();
    FakeStore store = new();
    InProcessAgentRuntime runtime = new(runner, store, maxConcurrentAgents: 1);
    AgentRecord first = RunningChild();

    Result<AgentId> firstStart = await runtime.Start(first, ct: TestContext.Current.CancellationToken);
    Assert.True(firstStart.IsSuccess);
    await runner.FirstCall.ConfigureAwait(true);

    Result<AgentId> blocked = await runtime.Start(RunningChild(), ct: TestContext.Current.CancellationToken);
    Assert.False(blocked.IsSuccess); // slot held by the gated child

    runner.Complete(CompletedOutcome(first.Id, "first done"));
    _ = await store.FirstUpdate.ConfigureAwait(true); // terminal update landed

    AgentRecord next = RunningChild();
    // Slot release races the awaited update; poll briefly until the freed slot admits the child.
    Result<AgentId> secondStart = await runtime.Start(next, ct: TestContext.Current.CancellationToken);
    DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
    while (!secondStart.IsSuccess && DateTime.UtcNow < deadline)
    {
      await Task.Delay(10, TestContext.Current.CancellationToken);
      secondStart = await runtime.Start(next, ct: TestContext.Current.CancellationToken);
    }

    Assert.True(secondStart.IsSuccess);
    Assert.Equal(next.Id, secondStart.Value);
  }
}
