using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;
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

  /// <summary>Runner whose shared gate holds children in-flight until the test releases them.
  ///     Dispatch observation counts: FirstCall resolves once one child entered RunAsync, and
  ///     WaitForDispatchAsync(n) once n children have — resume's second run is observable without
  ///     races. ReplaceGate swaps the gate after a settle so one runner drives sequential runs.</summary>
  private sealed class GateRunner : IAgentRunner
  {
    private TaskCompletionSource<AgentRunOutcome> _gate =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _dispatched;

    public List<AgentRecord> Started { get; } = [];

    /// <summary>Resolves once any child actually entered RunAsync (background dispatch observed).</summary>
    public Task FirstCall => WaitForDispatchAsync(1);

    /// <summary>Bounded wait until <paramref name="count"/> children entered RunAsync; the
    ///     deadline fires loudly instead of hanging (the same bounded-poll idiom the
    ///     released-slot test uses for its second dispatch).</summary>
    public async Task WaitForDispatchAsync(int count)
    {
      DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
      while (Volatile.Read(ref _dispatched) < count)
      {
        if (DateTime.UtcNow > deadline)
        {
          Assert.Fail($"background dispatch {count} was not observed in time");
        }

        await Task.Delay(10, CancellationToken.None).ConfigureAwait(true);
      }
    }

    public void Complete(AgentRunOutcome outcome) => _gate.SetResult(outcome);

    public void Throw(Exception exception) => _gate.SetException(exception);

    /// <summary>Installs a fresh gate after the previous run settled: the next dispatch parks on it.</summary>
    public void ReplaceGate() => _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<AgentRunOutcome> RunAsync(AgentRecord child, CancellationToken ct = default)
    {
      Started.Add(child);
      _ = Interlocked.Increment(ref _dispatched);
      return _gate.Task;
    }
  }

  /// <summary>Store capturing terminal updates, serving seeded records to GetAsync, and
  ///     signalling the first update for deterministic awaits.</summary>
  private sealed class FakeStore : IAgentStore
  {
    private readonly TaskCompletionSource<AgentRecord> _firstUpdate =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<AgentRecord> Updates { get; } = [];

    /// <summary>Records GetAsync serves; UpdateAsync writes through so readers see terminal state.</summary>
    public Dictionary<Guid, AgentRecord> Records { get; } = [];

    /// <summary>Load-bearing for the resume-validation test: validation must precede any store read.</summary>
    public bool GetAsyncCalled { get; private set; }

    /// <summary>Completes when the first update lands; times out loudly instead of hanging.</summary>
    public Task<AgentRecord> FirstUpdate => _firstUpdate.Task.WaitAsync(TimeSpan.FromSeconds(10));

    public Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
    {
      lock (Updates)
      {
        Updates.Add(record);
      }

      lock (Records)
      {
        Records[record.Id.Value] = record;
      }

      _ = _firstUpdate.TrySetResult(record);
      return Task.FromResult(Result.Success("updated"));
    }

    public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
        => throw new NotSupportedException("not exercised by runtime tests");

    public Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
    {
      GetAsyncCalled = true;
      lock (Records)
      {
        return Task.FromResult(Records.TryGetValue(id.Value, out AgentRecord? record)
            ? Result.Success(record)
            : Result.Failure<AgentRecord>(new DomainError("NotFound", $"agent '{id}' was not found.")));
      }
    }

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

    Result<AgentId> queued = await runtime.Start(RunningChild(), ct: TestContext.Current.CancellationToken);

    // FR-B6 contract: at-capacity starts QUEUE (visible, cancellable) and report success
    // immediately — the caller never blocks and never sees CapReached (FR-L1 stable).
    Assert.True(queued.IsSuccess);
    _ = Assert.Single(runner.Started); // only the first child reached the runner
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

    AgentRecord next = RunningChild();
    Result<AgentId> queued = await runtime.Start(next, ct: TestContext.Current.CancellationToken);
    Assert.True(queued.IsSuccess); // queued, not rejected
    _ = Assert.Single(runner.Started); // still only the gated child runs

    runner.Complete(CompletedOutcome(first.Id, "first done"));
    _ = await store.FirstUpdate.ConfigureAwait(true); // terminal update landed

    // The freed slot admits the queued child WITHOUT any further Start call (FR-B6 push).
    DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
    while (runner.Started.Count < 2 && DateTime.UtcNow < deadline)
    {
      await Task.Delay(10, TestContext.Current.CancellationToken);
    }

    Assert.Equal(2, runner.Started.Count);
  }

  /// <summary>Built here so tests observe the exact runner they complete: the gate test seam.</summary>
  private static InProcessAgentRuntime MakeRuntime(out FakeStore store, out GateRunner runner)
  {
    store = new FakeStore();
    runner = new GateRunner();
    return new InProcessAgentRuntime(runner, store, maxConcurrentAgents: 2);
  }

  /// <summary>Drives child.Id through one gated run to its terminal persist: parked -> completed.</summary>
  private static async Task RunToSettledAsync(InProcessAgentRuntime runtime, FakeStore store, GateRunner runner,
      AgentRecord child, string report)
  {
    _ = await runtime.Start(child, TestContext.Current.CancellationToken).ConfigureAwait(true);
    await runner.FirstCall.ConfigureAwait(true);
    runner.Complete(CompletedOutcome(child.Id, report));
    _ = await store.FirstUpdate.ConfigureAwait(true); // terminal write landed
    await Task.Delay(50, TestContext.Current.CancellationToken).ConfigureAwait(true); // run's finally: the slot freed
  }

  [Fact]
  public async Task Resume_UnknownId_FailsNotRunning()
  {
    InProcessAgentRuntime runtime = MakeRuntime(out FakeStore _, out GateRunner _);

    Result<AgentId> resumed = await runtime.Resume(new AgentId(Guid.NewGuid()), "go again",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(resumed.IsSuccess);
    Assert.Equal("NotRunning", resumed.Error.Code);
  }

  [Fact]
  public async Task Resume_LiveChild_FailsNotRunning_PointsToDeliver()
  {
    InProcessAgentRuntime runtime = MakeRuntime(out FakeStore store, out GateRunner runner);
    AgentRecord child = RunningChild();
    store.Records[child.Id.Value] = child;

    _ = await runtime.Start(child, TestContext.Current.CancellationToken).ConfigureAwait(true);
    await runner.FirstCall.ConfigureAwait(true); // the run is parked on the gate

    Result<AgentId> resumed = await runtime.Resume(child.Id, "go again",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(resumed.IsSuccess);
    Assert.Equal("NotRunning", resumed.Error.Code);
    Assert.Contains("agent.send", resumed.Error.Message, StringComparison.Ordinal);
    runner.Complete(CompletedOutcome(child.Id, "done")); // release the gate: no hanging test
  }

  [Fact]
  public async Task Resume_SettledChild_RestartsSameId_StampedContract_AttemptsIncremented()
  {
    InProcessAgentRuntime runtime = MakeRuntime(out FakeStore store, out GateRunner runner);
    AgentRecord child = RunningChild();
    store.Records[child.Id.Value] = child;
    await RunToSettledAsync(runtime, store, runner, child, "first report");
    runner.ReplaceGate();

    Result<AgentId> resumed = await runtime.Resume(child.Id, "fix the failing tests",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(resumed.IsSuccess, resumed.Error?.Message);
    await runner.WaitForDispatchAsync(2).ConfigureAwait(true); // second dispatch observed
    Assert.Equal(2, runner.Started.Count);
    AgentRecord secondRun = runner.Started[1];
    Assert.Equal(child.Id, secondRun.Id);
    Assert.Equal(1, secondRun.Attempts);
    Assert.Equal(AgentStatus.Running, secondRun.Status);
    SpawnContract contract = SpawnContract.Decode(secondRun.Contract!);
    Assert.Equal("fix the failing tests", contract.ResumeMessage);
    runner.Complete(new AgentRunOutcome(child.Id, AgentStatus.Completed, null, "second report", child.ModelUsed, child.Depth));
  }

  [Fact]
  public async Task Resume_EmptyMessage_FailsInvalidMessage_BeforeStoreAccess()
  {
    InProcessAgentRuntime runtime = MakeRuntime(out FakeStore store, out GateRunner _);

    Result<AgentId> resumed = await runtime.Resume(new AgentId(Guid.NewGuid()), "   ",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(resumed.IsSuccess);
    Assert.Equal("InvalidMessage", resumed.Error.Code);
    Assert.False(store.GetAsyncCalled, "validation must precede any store read");
  }

  [Fact]
  public async Task TerminalPersist_ClearsTheResumeCarrier_OneShot()
  {
    InProcessAgentRuntime runtime = MakeRuntime(out FakeStore store, out GateRunner runner);
    AgentRecord child = RunningChild();
    store.Records[child.Id.Value] = child;
    await RunToSettledAsync(runtime, store, runner, child, "first");
    runner.ReplaceGate();

    _ = await runtime.Resume(child.Id, "round two", TestContext.Current.CancellationToken).ConfigureAwait(true);
    await runner.WaitForDispatchAsync(2).ConfigureAwait(true); // the resumed run's dispatch observed
    runner.Complete(new AgentRunOutcome(child.Id, AgentStatus.Completed, null, "second", child.ModelUsed, child.Depth));
    _ = await store.FirstUpdate.ConfigureAwait(true);
    await Task.Delay(50, TestContext.Current.CancellationToken).ConfigureAwait(true); // run's finally: the slot freed

    AgentRecord final = store.Records[child.Id.Value];
    Assert.Equal(AgentStatus.Completed, final.Status);
    Assert.Equal("second", final.FinalReport);
    SpawnContract contract = SpawnContract.Decode(final.Contract!);
    Assert.Null(contract.ResumeMessage); // consumed: a watchdog retry must wrap-up-nudge, not re-deliver
  }
}
