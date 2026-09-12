using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;
namespace eThangAgent.AgentInfrastructure.Tests;

/// <summary>Integration proof for the full resume cycle (start -> settle -> resume -> settle) over a REAL
///     InProcessAgentRuntime: identity is kept across rounds, the one-shot resume carrier is stamped on
///     resume and consumed at terminal persist, each round is observable through WhenSettledAsync's fresh
///     settle source, and transcript appends survive the cycle without duplication. Fakes are copied from
///     InProcessAgentRuntimeTests (that file's established duplication pattern); the transcript members
///     are made REAL here (dictionary-backed) because this file's assertions exercise them.</summary>
public class InProcessAgentRuntimeResumeTests
{
  private static AgentRecord RunningChild(string prompt = "child task") =>
      AgentRecord.Spawned(new AgentId(Guid.NewGuid()), parentId: null, depth: 1,
          modelUsed: "mock/model", label: "test", taskPrompt: prompt, createdAt: DateTimeOffset.UtcNow);

  private static AgentRunOutcome CompletedOutcome(AgentId childId, string report) =>
      new(childId, AgentStatus.Completed, Reason: null, Report: report, ModelUsed: "mock/model", Depth: 1);

  /// <summary>Runner whose shared gate holds children in-flight until the test releases them.
  ///     Copied from InProcessAgentRuntimeTests: dispatch observation is race-free, and
  ///     ReplaceGate lets one runner drive sequential runs.</summary>
  private sealed class GateRunner : IAgentRunner
  {
    private TaskCompletionSource<AgentRunOutcome> _gate =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _dispatched;

    public List<AgentRecord> Started { get; } = [];

    /// <summary>Resolves once any child actually entered RunAsync (background dispatch observed).</summary>
    public Task FirstCall => WaitForDispatchAsync(1);

    /// <summary>Bounded wait until <paramref name="count"/> children entered RunAsync; the
    ///     deadline fires loudly instead of hanging (deadlock vigilance).</summary>
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

    /// <summary>Installs a fresh gate after the previous run settled: the next dispatch parks on it.</summary>
    public void ReplaceGate() => _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<AgentRunOutcome> RunAsync(AgentRecord child, CancellationToken ct = default)
    {
      Started.Add(child);
      _ = Interlocked.Increment(ref _dispatched);
      return _gate.Task;
    }
  }

  /// <summary>Store capturing terminal updates, serving seeded records to GetAsync, signalling the
  ///     first update for deterministic awaits, and KEEPING a real per-agent transcript (copied from
  ///     InProcessAgentRuntimeTests; AppendMessageAsync/GetTranscriptAsync are dictionary-backed here
  ///     because stage 8's assertions read through them).</summary>
  private sealed class FakeStore : IAgentStore
  {
    private readonly TaskCompletionSource<AgentRecord> _firstUpdate =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<Guid, List<Message>> _transcripts = [];

    public List<AgentRecord> Updates { get; } = [];

    /// <summary>Records GetAsync serves; UpdateAsync writes through so readers see terminal state.</summary>
    public Dictionary<Guid, AgentRecord> Records { get; } = [];

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
      lock (Records)
      {
        return Task.FromResult(Records.TryGetValue(id.Value, out AgentRecord? record)
            ? Result.Success(record)
            : Result.Failure<AgentRecord>(new DomainError("NotFound", $"agent '{id}' was not found.")));
      }
    }

    public Task<Result<string>> AppendMessageAsync(AgentId id, Message message,
        CancellationToken ct = default)
    {
      lock (_transcripts)
      {
        if (!_transcripts.TryGetValue(id.Value, out List<Message>? transcript))
        {
          transcript = [];
          _transcripts[id.Value] = transcript;
        }

        transcript.Add(message);
      }

      return Task.FromResult(Result.Success(id.ToString()));
    }

    public Task<Result<string>> ReplaceTranscriptAsync(AgentId id, IReadOnlyList<Message> messages, CancellationToken ct = default)
    {
      lock (_transcripts)
      {
        _transcripts[id.Value] = [.. messages];
      }

      return Task.FromResult(Result.Success(id.ToString()));
    }

    public Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(
      AgentId id, CancellationToken ct = default)
    {
      lock (_transcripts)
      {
        IReadOnlyList<Message> snapshot = _transcripts.TryGetValue(id.Value, out List<Message>? transcript)
            ? transcript.ToArray()
            : [];
        return Task.FromResult(Result.Success(snapshot));
      }
    }

    public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId,
        CancellationToken ct = default) => throw new NotSupportedException("not exercised by runtime tests");

    public Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
        => throw new NotSupportedException("not exercised by runtime tests");
  }

  /// <summary>Built here so the test observes the exact runner it completes (copied helper).</summary>
  private static InProcessAgentRuntime MakeRuntime(out FakeStore store, out GateRunner runner)
  {
    store = new FakeStore();
    runner = new GateRunner();
    return new InProcessAgentRuntime(runner, store, maxConcurrentAgents: 2);
  }

  /// <summary>Drives child.Id through one gated run to its terminal persist (copied helper):
  ///     parked -> completed.</summary>
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
  public async Task FullCycle_Start_Settle_Resume_Settle_KeepsIdentityAndCarriesTheFixRound()
  {
    InProcessAgentRuntime runtime = MakeRuntime(out FakeStore store, out GateRunner runner);
    AgentRecord child = RunningChild();
    store.Records[child.Id.Value] = child; // resume validation reads this row

    // 1. start a child, hold it on the gate, complete it (its transcript appends first-round rows).
    await RunToSettledAsync(runtime, store, runner, child, "first report").ConfigureAwait(true);
    Message firstUser = new(Role.User, "round-one prompt", DateTimeOffset.UtcNow);
    Message firstAssistant = new(Role.Assistant, "round-one done", DateTimeOffset.UtcNow);
    _ = await store.AppendMessageAsync(child.Id, firstUser, TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = await store.AppendMessageAsync(child.Id, firstAssistant, TestContext.Current.CancellationToken).ConfigureAwait(true);

    // 2. WhenSettledAsync returns the first outcome.
    Result<AgentRunOutcome> firstSettle = await runtime.WhenSettledAsync(child.Id, TestContext.Current.CancellationToken)
        .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(firstSettle.IsSuccess, firstSettle.Error?.Message);
    Assert.Equal("first report", firstSettle.Value.Report);

    runner.ReplaceGate(); // the resumed run parks on a fresh gate
    Message betweenRounds = new(Role.User, "fix the failing tests", DateTimeOffset.UtcNow);
    _ = await store.AppendMessageAsync(child.Id, betweenRounds, TestContext.Current.CancellationToken).ConfigureAwait(true);

    // 3. Resume succeeds on the SAME id.
    Result<AgentId> resumed = await runtime.Resume(child.Id, "fix the failing tests", TestContext.Current.CancellationToken)
        .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(resumed.IsSuccess, resumed.Error?.Message);
    Assert.Equal(child.Id, resumed.Value);

    // 4. the runner sees a SECOND dispatch: same id, Attempts == 1, contract stamped.
    await runner.WaitForDispatchAsync(2).ConfigureAwait(true);
    Assert.Equal(2, runner.Started.Count);
    AgentRecord secondRun = runner.Started[1];
    Assert.Equal(child.Id, secondRun.Id);
    Assert.Equal(1, secondRun.Attempts);
    Assert.Equal(AgentStatus.Running, secondRun.Status);
    SpawnContract stamped = SpawnContract.Decode(secondRun.Contract!);
    Assert.Equal("fix the failing tests", stamped.ResumeMessage);

    // 5. complete the second run with a new report (its transcript appends a second-round row).
    Message secondAssistant = new(Role.Assistant, "round-two done", DateTimeOffset.UtcNow);
    _ = await store.AppendMessageAsync(child.Id, secondAssistant, TestContext.Current.CancellationToken).ConfigureAwait(true);
    runner.Complete(CompletedOutcome(child.Id, "second report"));

    // 6. WhenSettledAsync returns the SECOND report (fresh settle source for the resumed run).
    Result<AgentRunOutcome> secondSettle = await runtime.WhenSettledAsync(child.Id, TestContext.Current.CancellationToken)
        .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(secondSettle.IsSuccess, secondSettle.Error?.Message);
    Assert.Equal("second report", secondSettle.Value.Report);

    // 7. the store's final row: Completed, new FinalReport, carrier cleared (one-shot consumed).
    AgentRecord final = store.Records[child.Id.Value];
    Assert.Equal(AgentStatus.Completed, final.Status);
    Assert.Equal("second report", final.FinalReport);
    SpawnContract cleared = SpawnContract.Decode(final.Contract!);
    Assert.Null(cleared.ResumeMessage);

    // 8. messages appended between rounds survive: first-round + appended + second-round, in order, no duplication.
    Result<IReadOnlyList<Message>> transcript = await store.GetTranscriptAsync(child.Id, TestContext.Current.CancellationToken)
        .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(transcript.IsSuccess, transcript.Error?.Message);
    Assert.Equal(4, transcript.Value.Count);
    Assert.Equal("round-one prompt", transcript.Value[0].Content);
    Assert.Equal("round-one done", transcript.Value[1].Content);
    Assert.Equal("fix the failing tests", transcript.Value[2].Content);
    Assert.Equal("round-two done", transcript.Value[3].Content);
  }
}
