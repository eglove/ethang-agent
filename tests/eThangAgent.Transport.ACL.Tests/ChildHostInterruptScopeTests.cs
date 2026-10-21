using eThangAgent.AgentDomain;
using eThangAgent.AgentInfrastructure;
using eThangAgent.ChildHost;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>Task 1 (scoped stop): a subtree-root interrupt over the wire cancels ONLY
///     the ids in the root's subtree among the host's active children — another
///     session's child (a sibling root's descendant) is never touched, and a NULL-id
///     interrupt remains the legacy blanket cancel. The rig registers active children
///     with parent edges the way RunChildAsync does (AttachActiveChild), no real child
///     runs; the serve loop stays alive throughout.</summary>
public class ChildHostInterruptScopeTests
{
  [Fact]
  public async Task SubtreeRootInterrupt_CancelsOnlyThatSubtreesActiveChildren()
  {
    (ChildHostServer server, NamedPipeChildTransport appSide, Func<Task> _) = await StartRigAsync().ConfigureAwait(true);
    try
    {
      Guid sessionRoot = Guid.NewGuid();
      Guid ownChild = Guid.NewGuid();
      Guid ownGrandchild = Guid.NewGuid();
      (Guid otherId, CancellationTokenSource otherCts) = NewActive();
      // Named decision (CA2000): the CTSs' ownership transfers to the server's active
      // set — the run's finally (or the test's observation) owns their lifetime.
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
      RecordingRuntime runtime = new();
      server.AttachActiveChild(sessionRoot, parentId: null, NewCts(), runtime);
      server.AttachActiveChild(ownChild, sessionRoot, NewCts(), runtime);
      server.AttachActiveChild(ownGrandchild, ownChild, NewCts(), runtime);
      server.AttachActiveChild(otherId, parentId: null, otherCts, runtime);
#pragma warning restore CA2000 // Call IDisposable.Dispose on object created by

      await appSide.SendAsync(new TransportEnvelope("interrupt",
          System.Text.Json.JsonSerializer.Serialize(new InterruptCommand(sessionRoot)), NextSeq()),
          TestContext.Current.CancellationToken).ConfigureAwait(true);

      // Every member of the session's subtree is interrupted through the child
      // containers' runtimes — root, child, grandchild, deepest-first.
      await WaitUntil(() => runtime.Interrupts.Count == 3, TimeSpan.FromSeconds(10)).ConfigureAwait(true);
      Assert.Contains(new AgentId(sessionRoot), runtime.Interrupts);
      Assert.Contains(new AgentId(ownChild), runtime.Interrupts);
      Assert.Contains(new AgentId(ownGrandchild), runtime.Interrupts);
      // The OTHER session's child (outside the subtree) is never interrupted.
      Assert.DoesNotContain(runtime.Interrupts, i => i.Value == otherId);
    }
    finally
    {
      await appSide.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task NullIdInterrupt_RemainsTheLegacyBlanketCancel()
  {
    (ChildHostServer server, NamedPipeChildTransport appSide, Func<Task> _) = await StartRigAsync().ConfigureAwait(true);
    try
    {
      (Guid idA, CancellationTokenSource ctsA) = NewActive();
      (Guid idB, CancellationTokenSource ctsB) = NewActive();
      // Named decision (CA2000): ownership transfers to the server's active set.
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
      server.AttachActiveChild(idA, parentId: null, ctsA, new RecordingRuntime());
      server.AttachActiveChild(idB, parentId: null, ctsB, new RecordingRuntime());
#pragma warning restore CA2000 // Call IDisposable.Dispose on object created by

      await appSide.SendAsync(new TransportEnvelope("interrupt",
          System.Text.Json.JsonSerializer.Serialize(new InterruptCommand(null)), NextSeq()),
          TestContext.Current.CancellationToken).ConfigureAwait(true);

      await WaitUntil(() => Cancelled(server, idA) && Cancelled(server, idB),
          TimeSpan.FromSeconds(10)).ConfigureAwait(true);
    }
    finally
    {
      await appSide.DisposeAsync().ConfigureAwait(true);
    }
  }


  [Fact]
  public async Task SubtreeRootInterrupt_CancelsTheRealRunToken_NotJustBookkeepingState()
  {
    (ChildHostServer server, NamedPipeChildTransport appSide, Func<Task> _) = await StartRigAsync().ConfigureAwait(true);
    try
    {
      Guid sessionRoot = Guid.NewGuid();
      Guid childId = Guid.NewGuid();
      AgentRecord record = AgentRecord.Spawned(new AgentId(childId), new AgentId(sessionRoot), 1,
          "mock/model", "scoped-stop", "park until interrupted", DateTimeOffset.UtcNow);
      ParkingRunRunner runner = new();
      InProcessAgentRuntime runtime = new(runner, new SilentStore(), maxConcurrentAgents: 1);
      _ = await runtime.Start(record, TestContext.Current.CancellationToken).ConfigureAwait(true);
      await runner.FirstCall.ConfigureAwait(true); // the REAL run is in-flight, parked on its token

      CancellationTokenSource bookkeeping = new();
      // Named decision (CA2000): ownership transfers to the server's active set.
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
      server.AttachActiveChild(childId, sessionRoot, bookkeeping, runtime);
#pragma warning restore CA2000 // Call IDisposable.Dispose on object created by

      await appSide.SendAsync(new TransportEnvelope("interrupt",
          System.Text.Json.JsonSerializer.Serialize(new InterruptCommand(sessionRoot)), NextSeq()),
          TestContext.Current.CancellationToken).ConfigureAwait(true);

      // The REAL run token — the one the child's loop executes under — is cancelled.
      await WaitUntil(() => runner.ObservedToken.IsCancellationRequested,
          TimeSpan.FromSeconds(10)).ConfigureAwait(true);
      // The bookkeeping waiter token is deliberately NOT cancelled: a cancelled wait
      // token would make WhenSettledAsync return a bare Cancelled failure, and the
      // run's real Interrupted outcome would never reach the app as a settle envelope.
      // The run settles itself and completes the wait naturally.
      Assert.False(bookkeeping.IsCancellationRequested);
    }
    finally
    {
      await appSide.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task RootTurnInterrupt_DoesNotCancelAnotherSessionsChild()
  {
    (ChildHostServer server, NamedPipeChildTransport appSide, Func<Task> _) = await StartRigAsync().ConfigureAwait(true);
    try
    {
      // A child belonging to a DIFFERENT session, live in this shared host.
      (Guid otherId, CancellationTokenSource otherCts) = NewActive();
      RecordingRuntime otherRuntime = new();
      // Named decision (CA2000): ownership transfers to the server's active set.
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
      server.AttachActiveChild(otherId, parentId: null, otherCts, otherRuntime);
#pragma warning restore CA2000 // Call IDisposable.Dispose on object created by

      // A root-turn stop: TurnId present, RecordId NULL — the exact shape the Desktop's
      // stop button sends. It must reach ONLY that turn.
      await appSide.SendAsync(new TransportEnvelope("interrupt",
          System.Text.Json.JsonSerializer.Serialize(new InterruptCommand(null, "turn-stop")), NextSeq()),
          TestContext.Current.CancellationToken).ConfigureAwait(true);

      // Give the host ample time to (wrongly) enact a blanket cancel, then assert it did not.
      await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken).ConfigureAwait(true);

      // THE INVARIANT: another session's child run survives a root-turn stop. A null
      // RecordId must NOT be read as 'cancel everything' when a TurnId was supplied.
      Assert.False(otherCts.IsCancellationRequested,
          "a root-turn interrupt must not cancel another session's child run");
      Assert.DoesNotContain(otherRuntime.Interrupts, i => i.Value == otherId);
    }
    finally
    {
      await appSide.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task RootTurnInterrupt_CancelsTheTargetedTurn()
  {
    (ChildHostServer server, NamedPipeChildTransport appSide, Func<Task> _) = await StartRigAsync().ConfigureAwait(true);
    try
    {
      // A live ROOT turn registered under its turn id, exactly as HandleRootTurnAsync does.
      using CancellationTokenSource turnCts = new();
      server.RegisterRootTurn("turn-live", turnCts);

      await appSide.SendAsync(new TransportEnvelope("interrupt",
          System.Text.Json.JsonSerializer.Serialize(new InterruptCommand(null, "turn-live")), NextSeq()),
          TestContext.Current.CancellationToken).ConfigureAwait(true);

      // The scoped stop must still DO its job: the targeted turn's token is cancelled,
      // so the host actually stops rather than merely abandoning the app's local wait.
      await WaitUntil(() => turnCts.IsCancellationRequested, TimeSpan.FromSeconds(10)).ConfigureAwait(true);
    }
    finally
    {
      await appSide.DisposeAsync().ConfigureAwait(true);
    }
  }

  /// <summary>Recording runtime: captures the ids interrupts are enacted against —
  ///     the proof that scoped interrupts reach the child containers' runtimes.</summary>
  private sealed class RecordingRuntime : IAgentRuntime
  {
    public List<AgentId> Interrupts { get; } = [];

    public Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success(record.Id));
    public Task<Result<AgentRunOutcome>> WhenSettledAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<AgentRunOutcome>(new DomainError("NotFound", "not exercised")));
    public Task<Result<AgentId>> Resume(AgentId id, string message, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<AgentId>(new DomainError("ResumeUnsupported", "not exercised")));
    public Result<bool> Deliver(AgentId id, PendingMessage message) => Result.Success(true);
    public void InterruptSubtree(AgentId rootOfSubtree) { }
    public void Interrupt(AgentId? childId = null)
    {
      if (childId is { } id)
      {
        Interrupts.Add(id);
      }
    }
  }

  /// <summary>Runner whose run parks on its cancellation token until interrupted —
  ///     the real child-run shape: ObservedToken IS the token the loop executes under.</summary>
  private sealed class ParkingRunRunner : IAgentRunner
  {
    private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CancellationToken ObservedToken { get; private set; }

    public Task FirstCall => _first.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

    public async Task<AgentRunOutcome> RunAsync(AgentRecord child, CancellationToken ct = default)
    {
      ObservedToken = ct;
      _ = _first.TrySetResult();
      try
      {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        // bounded: the interrupt token is the only thing that settles this await
      }

      return new AgentRunOutcome(child.Id, AgentStatus.Failed,
          AgentFailureReason.Interrupted, "child agent was interrupted.", child.ModelUsed, child.Depth);
    }
  }

  /// <summary>Accepts every write, serves nothing — the runtime's terminal update path
  ///     needs a store that does not throw; assertions never read it.</summary>
  private sealed class SilentStore : IAgentStore
  {
    public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success("saved"));
    public Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success("updated"));
    public Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<AgentRecord>(new DomainError("NotFound", "not found")));
    public Task<Result<string>> AppendMessageAsync(AgentId id, Message message, CancellationToken ct = default)
        => Task.FromResult(Result.Success("appended"));
    public Task<Result<string>> ReplaceTranscriptAsync(AgentId id, IReadOnlyList<Message> messages, CancellationToken ct = default)
        => Task.FromResult(Result.Success(id.ToString()));
    public Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<Message>>([]));
    public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([]));
    public Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([]));
  }
  private static (Guid Id, CancellationTokenSource Cts) NewActive()
      => (Guid.NewGuid(), NewCts());

  private static CancellationTokenSource NewCts() => new();

  private static bool Cancelled(ChildHostServer server, Guid id)
      => server.ActiveToken(id) is { } token && token.IsCancellationRequested;

  private static async Task WaitUntil(Func<bool> condition, TimeSpan limit)
  {
    DateTimeOffset deadline = DateTimeOffset.UtcNow + limit;
    while (!condition())
    {
      if (DateTimeOffset.UtcNow > deadline)
      {
        Assert.Fail("condition not met within the limit");
      }

      await Task.Delay(50, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }
  }

  private static long _seq;

  private static long NextSeq() => Interlocked.Increment(ref _seq);

  private static async Task<(ChildHostServer Server, NamedPipeChildTransport App, Func<Task> Serve)> StartRigAsync()
  {
    string settings = Path.Combine(Path.GetTempPath(), "ethang-interruptrig-" + Guid.NewGuid().ToString("N") + ".json");
    await File.WriteAllTextAsync(settings, /*lang=json,strict*/ "{\"OpenRouter\":{\"ApiKey\":null},\"Zai\":{\"ApiKey\":null}}",
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    ChildHostServer server = new(settings, Path.Combine(Path.GetTempPath(), "ethang-interruptrig.db"));
    string pipeName = "ethang-interruptrig-" + Guid.NewGuid().ToString("N");
    Task<NamedPipeChildTransport> accept = NamedPipeChildTransport.AcceptAppAsync(pipeName, TestContext.Current.CancellationToken);
    NamedPipeChildTransport app = await NamedPipeChildTransport.ConnectToHostAsync(pipeName,
        TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    NamedPipeChildTransport hostSide = await accept.WaitAsync(TimeSpan.FromSeconds(10),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    server.AttachTransport(hostSide);
    Task serve = server.ServeAsync();
    return (server, app, () => serve);
  }
}
