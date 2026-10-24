using System.Collections.Concurrent;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentInfrastructure.Tests;

/// <summary>Issue #238: the concurrency-boundary queue is first-class. A spawn past
///     MaxConcurrentAgents is persisted Queued, observable through agent.wait (which
///     blocks instead of failing NotFound), reachable by agent.send, terminalized by
///     agent.cancel, and never lost — an admission fault settles it with a well-formed
///     failure instead of leaving a Running row with no run behind it.</summary>
public class InProcessAgentRuntimeQueueTests
{
  private static AgentRecord QueuedChild(string prompt = "queued child", int priority = 0)
  {
    SpawnContract contract = new() { PreemptGrant = priority > 0 };
    return AgentRecord.Spawned(AgentId.NewId(), parentId: null, depth: 1,
        modelUsed: "mock/model", label: "test", taskPrompt: prompt, createdAt: DateTimeOffset.UtcNow,
        contract: contract) with
    { Status = AgentStatus.Queued };
  }

  /// <summary>Runner with one gate per child: each run parks until the test completes
  ///     that child, so queue admission order is observable without races.</summary>
  private sealed class KeyedGateRunner : IAgentRunner
  {
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<AgentRunOutcome>> _gates = [];
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<AgentRecord>> _dispatched = [];

    public List<AgentRecord> Started { get; } = [];

    public async Task<AgentRecord> DispatchedAsync(AgentId id)
    {
      TaskCompletionSource<AgentRecord> source = _dispatched.GetOrAdd(id.Value, _ => NewSource<AgentRecord>());
      return await source.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)
          .ConfigureAwait(true);
    }

    /// <summary>True when the id never reached the runner inside the window — the
    ///     bounded negative observation a "still queued" assertion needs.</summary>
    public async Task<bool> NeverDispatchedAsync(AgentId id, TimeSpan window)
    {
      try
      {
        _ = await DispatchedAsync(id).WaitAsync(window, TestContext.Current.CancellationToken).ConfigureAwait(true);
        return false;
      }
      catch (TimeoutException)
      {
        return true;
      }
    }

    public void Complete(AgentId id, string report)
      => _gates.GetOrAdd(id.Value, _ => NewSource<AgentRunOutcome>()).SetResult(
          new AgentRunOutcome(id, AgentStatus.Completed, null, report, "mock/model", 1));

    public Task<AgentRunOutcome> RunAsync(AgentRecord child, CancellationToken ct = default)
    {
      lock (Started)
      {
        Started.Add(child);
      }

      _ = _dispatched.GetOrAdd(child.Id.Value, _ => NewSource<AgentRecord>()).TrySetResult(child);
      return _gates.GetOrAdd(child.Id.Value, _ => NewSource<AgentRunOutcome>()).Task;
    }

    private static TaskCompletionSource<T> NewSource<T>()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
  }

  /// <summary>Store that serves seeded records and writes updates through, so a reader
  ///     observes exactly what the runtime persisted.</summary>
  private sealed class RecordingStore : IAgentStore
  {
    public ConcurrentDictionary<Guid, AgentRecord> Records { get; } = [];

    public List<AgentRecord> Updates { get; } = [];

    public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
    {
      Records[record.Id.Value] = record;
      return Task.FromResult(Result.Success(record.Id.ToString()));
    }

    public Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
    {
      lock (Updates)
      {
        Updates.Add(record);
      }

      Records[record.Id.Value] = record;
      return Task.FromResult(Result.Success(record.Id.ToString()));
    }

    public Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
      => Task.FromResult(Records.TryGetValue(id.Value, out AgentRecord? record)
          ? Result.Success(record)
          : Result.Failure<AgentRecord>(new DomainError("NotFound", $"agent '{id}' was not found.")));

    public Task<Result<string>> AppendMessageAsync(AgentId id, Message message, CancellationToken ct = default)
      => Task.FromResult(Result.Success(id.ToString()));

    public Task<Result<string>> ReplaceTranscriptAsync(AgentId id, IReadOnlyList<Message> messages,
        CancellationToken ct = default) => Task.FromResult(Result.Success(id.ToString()));

    public Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(AgentId id, CancellationToken ct = default)
      => Task.FromResult(Result.Success<IReadOnlyList<Message>>([]));

    public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default)
      => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>(
          [.. Records.Values.Where(r => r.ParentId == parentId)]));

    public Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
      => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([.. Records.Values]));
  }

  private static async Task<bool> SpinUntilAsync(Func<bool> condition)
  {
    DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
    while (!condition())
    {
      if (DateTime.UtcNow > deadline)
      {
        return false;
      }

      await Task.Delay(10, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    return true;
  }

  [Fact]
  public async Task WhenSettledAsync_QueuedSpawn_BlocksUntilItRunsAndSettles()
  {
    KeyedGateRunner runner = new();
    RecordingStore store = new();
    InProcessAgentRuntime runtime = new(runner, store, maxConcurrentAgents: 1);
    AgentRecord running = QueuedChild("first") with { Status = AgentStatus.Running };
    AgentRecord queued = QueuedChild("second");
    store.Records[running.Id.Value] = running;
    store.Records[queued.Id.Value] = queued;

    _ = await runtime.Start(running, TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = await runner.DispatchedAsync(running.Id).ConfigureAwait(true);
    _ = await runtime.Start(queued, TestContext.Current.CancellationToken).ConfigureAwait(true);

    // The queued child is awaitable: the wait parks instead of failing NotFound.
    Task<Result<AgentRunOutcome>> waiting = runtime.WhenSettledAsync(queued.Id, TestContext.Current.CancellationToken);
    Task settledEarly = await Task.WhenAny(waiting, Task.Delay(200, TestContext.Current.CancellationToken))
        .ConfigureAwait(true);
    Assert.NotSame(waiting, settledEarly);

    runner.Complete(running.Id, "first done");
    _ = await runner.DispatchedAsync(queued.Id).ConfigureAwait(true); // the freed slot admitted it
    runner.Complete(queued.Id, "second done");

    Result<AgentRunOutcome> outcome = await waiting.WaitAsync(TimeSpan.FromSeconds(10),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(outcome.IsSuccess, outcome.Error?.Message);
    Assert.Equal(AgentStatus.Completed, outcome.Value.Status);
    Assert.Equal("second done", outcome.Value.Report);
  }

  [Fact]
  public async Task QueuedSpawn_StaysQueuedInTheStore_AndFlipsToRunningOnAdmission()
  {
    KeyedGateRunner runner = new();
    RecordingStore store = new();
    InProcessAgentRuntime runtime = new(runner, store, maxConcurrentAgents: 1);
    AgentRecord running = QueuedChild("first") with { Status = AgentStatus.Running };
    AgentRecord queued = QueuedChild("second");
    store.Records[running.Id.Value] = running;
    store.Records[queued.Id.Value] = queued;

    _ = await runtime.Start(running, TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = await runner.DispatchedAsync(running.Id).ConfigureAwait(true);
    _ = await runtime.Start(queued, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal(AgentStatus.Queued, store.Records[queued.Id.Value].Status);

    runner.Complete(running.Id, "first done");
    _ = await runner.DispatchedAsync(queued.Id).ConfigureAwait(true);

    bool flipped = await SpinUntilAsync(() => store.Records[queued.Id.Value].Status is AgentStatus.Running)
        .ConfigureAwait(true);
    Assert.True(flipped, "the admitted child's row must read Running once its run begins");
    runner.Complete(queued.Id, "second done");
  }

  [Fact]
  public async Task SettledRun_AdmitsTheQueuedHead_NotAConcurrentStart()
  {
    KeyedGateRunner runner = new();
    RecordingStore store = new();
    InProcessAgentRuntime runtime = new(runner, store, maxConcurrentAgents: 1);
    AgentRecord first = QueuedChild("first") with { Status = AgentStatus.Running };
    AgentRecord second = QueuedChild("second");
    AgentRecord third = QueuedChild("third");
    foreach (AgentRecord record in new[] { first, second, third })
    {
      store.Records[record.Id.Value] = record;
    }

    _ = await runtime.Start(first, TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = await runner.DispatchedAsync(first.Id).ConfigureAwait(true);
    _ = await runtime.Start(second, TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = await runtime.Start(third, TestContext.Current.CancellationToken).ConfigureAwait(true);

    runner.Complete(first.Id, "first done");

    // FIFO at the boundary: the head waiter starts first, never the later arrival.
    _ = await runner.DispatchedAsync(second.Id).ConfigureAwait(true);
    Assert.True(await runner.NeverDispatchedAsync(third.Id, TimeSpan.FromMilliseconds(200)).ConfigureAwait(true),
        "the second queued child must not start while the single slot is taken");
    runner.Complete(second.Id, "second done");
    _ = await runner.DispatchedAsync(third.Id).ConfigureAwait(true);
    runner.Complete(third.Id, "third done");
  }

  [Fact]
  public async Task Interrupt_QueuedSpawn_TerminalizesTheRecordAndSettlesItsWaiters()
  {
    KeyedGateRunner runner = new();
    RecordingStore store = new();
    InProcessAgentRuntime runtime = new(runner, store, maxConcurrentAgents: 1);
    AgentRecord running = QueuedChild("first") with { Status = AgentStatus.Running };
    AgentRecord queued = QueuedChild("second");
    store.Records[running.Id.Value] = running;
    store.Records[queued.Id.Value] = queued;

    _ = await runtime.Start(running, TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = await runner.DispatchedAsync(running.Id).ConfigureAwait(true);
    _ = await runtime.Start(queued, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Task<Result<AgentRunOutcome>> waiting = runtime.WhenSettledAsync(queued.Id, TestContext.Current.CancellationToken);

    runtime.Interrupt(queued.Id);

    Result<AgentRunOutcome> outcome = await waiting.WaitAsync(TimeSpan.FromSeconds(10),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(outcome.IsSuccess, outcome.Error?.Message);
    Assert.Equal(AgentStatus.Failed, outcome.Value.Status);
    Assert.Equal(AgentFailureReason.Interrupted, outcome.Value.Reason);

    AgentRecord persisted = store.Records[queued.Id.Value];
    Assert.Equal(AgentStatus.Failed, persisted.Status);
    Assert.Equal(AgentFailureReason.Interrupted, persisted.FailureReason);
    _ = Assert.NotNull(persisted.CompletedAt);

    // The dropped spawn never runs, and the freed queue leaves the running child alone.
    runner.Complete(running.Id, "first done");
    Assert.True(await runner.NeverDispatchedAsync(queued.Id, TimeSpan.FromMilliseconds(200)).ConfigureAwait(true),
        "an interrupted queued spawn must never reach the runner");
  }

  [Fact]
  public async Task InterruptAll_QueuedSpawns_AreTerminalizedNotDropped()
  {
    KeyedGateRunner runner = new();
    RecordingStore store = new();
    InProcessAgentRuntime runtime = new(runner, store, maxConcurrentAgents: 1);
    AgentRecord running = QueuedChild("first") with { Status = AgentStatus.Running };
    AgentRecord queued = QueuedChild("second");
    store.Records[running.Id.Value] = running;
    store.Records[queued.Id.Value] = queued;

    _ = await runtime.Start(running, TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = await runner.DispatchedAsync(running.Id).ConfigureAwait(true);
    _ = await runtime.Start(queued, TestContext.Current.CancellationToken).ConfigureAwait(true);

    runtime.Interrupt();

    bool terminalized = await SpinUntilAsync(() =>
        store.Records[queued.Id.Value].Status is AgentStatus.Failed).ConfigureAwait(true);
    Assert.True(terminalized, "a blanket interrupt must terminalize queued spawns, never silently drop them");
    Assert.Equal(AgentFailureReason.Interrupted, store.Records[queued.Id.Value].FailureReason);
  }

  [Fact]
  public async Task AdmissionFault_TerminalizesTheRecord_AndSettlesItsWaiterWithTheFailure()
  {
    KeyedGateRunner runner = new();
    RecordingStore store = new();
    InProcessAgentRuntime runtime = new(runner, store, maxConcurrentAgents: 1);
    AgentRecord running = QueuedChild("first") with { Status = AgentStatus.Running };
    // A corrupt contract column is the real-world admission fault: the runtime cannot
    // decode the agreement it is about to enact.
    AgentRecord corrupt = QueuedChild("corrupt") with { Contract = "{not-json" };
    store.Records[running.Id.Value] = running;
    store.Records[corrupt.Id.Value] = corrupt;

    _ = await runtime.Start(running, TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = await runner.DispatchedAsync(running.Id).ConfigureAwait(true);
    _ = await runtime.Start(corrupt, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Task<Result<AgentRunOutcome>> waiting = runtime.WhenSettledAsync(corrupt.Id, TestContext.Current.CancellationToken);

    runner.Complete(running.Id, "first done");

    Result<AgentRunOutcome> outcome = await waiting.WaitAsync(TimeSpan.FromSeconds(10),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(outcome.IsSuccess, outcome.Error?.Message);
    Assert.Equal(AgentStatus.Failed, outcome.Value.Status);

    bool failed = await SpinUntilAsync(() =>
        store.Records[corrupt.Id.Value].Status is AgentStatus.Failed).ConfigureAwait(true);
    Assert.True(failed, "an admission fault must terminalize the record, never leave it Running");
    Assert.Contains("admission", store.Records[corrupt.Id.Value].FinalReport!, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task Deliver_QueuedSpawn_IsAcceptedIntoItsMailbox()
  {
    KeyedGateRunner runner = new();
    RecordingStore store = new();
    ChildMailboxRegistry registry = new();
    InProcessAgentRuntime runtime = new(runner, store, maxConcurrentAgents: 1, mailboxes: registry);
    AgentRecord running = QueuedChild("first") with { Status = AgentStatus.Running };
    AgentRecord queued = QueuedChild("second");
    store.Records[running.Id.Value] = running;
    store.Records[queued.Id.Value] = queued;

    _ = await runtime.Start(running, TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = await runner.DispatchedAsync(running.Id).ConfigureAwait(true);
    _ = await runtime.Start(queued, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Result<bool> delivered = runtime.Deliver(queued.Id,
        PendingMessage.New("steer while queued", MessageUrgency.Normal, "parent"));

    Assert.True(delivered.IsSuccess, delivered.Error?.Message);
    Assert.True(runtime.InboxFor(queued.Id)!.TryTake(out string steered, out Guid _), "the queued child's mailbox must hold the message");
    Assert.Equal("steer while queued", steered);
    runner.Complete(running.Id, "first done");
    runner.Complete(queued.Id, "second done");
  }

  [Fact]
  public async Task Resume_QueuedSpawn_FailsNotRunning_InsteadOfQueueingItTwice()
  {
    KeyedGateRunner runner = new();
    RecordingStore store = new();
    InProcessAgentRuntime runtime = new(runner, store, maxConcurrentAgents: 1);
    AgentRecord running = QueuedChild("first") with { Status = AgentStatus.Running };
    AgentRecord queued = QueuedChild("second");
    store.Records[running.Id.Value] = running;
    store.Records[queued.Id.Value] = queued;

    _ = await runtime.Start(running, TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = await runner.DispatchedAsync(running.Id).ConfigureAwait(true);
    _ = await runtime.Start(queued, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Result<AgentId> resumed = await runtime.Resume(queued.Id, "go again",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(resumed.IsSuccess);
    Assert.Equal("NotRunning", resumed.Error.Code);
    runner.Complete(running.Id, "first done");
    runner.Complete(queued.Id, "second done");
  }
}
