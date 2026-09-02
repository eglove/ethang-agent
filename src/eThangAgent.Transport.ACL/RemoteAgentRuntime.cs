using System.Collections.Concurrent;
using System.Text.Json;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Transport.ACL;

/// <summary>The app-side runtime for out-of-process children (FR-X1/X2/X3): implements the
///     domain's IAgentRuntime by translating actor operations to wire envelopes over the
///     named-pipe transport. Delivery semantics: at-least-once on the wire with host acks —
///     presented to the domain as the same surface the in-process runtime exposes (D11).
///     OwnedChildren records which ids are remote so orphan repair can be exact (FR-L8).</summary>
public sealed class RemoteAgentRuntime : IAgentRuntime
{
  private NamedPipeChildTransport? _transport;

  /// <summary>Creates a DISCONNECTED runtime (composition, remote mode): AttachAsync
  ///     owns connecting, starting the pump, and reading the host's declared set.</summary>
  public RemoteAgentRuntime()
  {
  }

  /// <summary>Creates a runtime over an already-connected transport (tests, replays).</summary>
  public RemoteAgentRuntime(NamedPipeChildTransport? transport)
      => _transport = transport;

  private readonly ConcurrentDictionary<Guid, TaskCompletionSource<AgentRunOutcome>> _settling = [];
  private readonly ConcurrentDictionary<Guid, byte> _owned = [];
  private Guid[] _declaredLive = [];
  private long _sequence;

  /// <summary>The host's most recently declared live child set (R3.2's exact orphan
  ///     resolution input). Refreshed by the pump on every declare envelope — the
  ///     host sends it on connect and after each start/settle — so a snapshot read
  ///     after re-attach reflects children the host kept running.</summary>
  public IReadOnlyCollection<Guid> DeclaredLiveChildren => _declaredLive;

  /// <summary>Ids this runtime has started remotely (exact ownership for orphan repair).</summary>
  public IReadOnlyCollection<Guid> OwnedChildren
  {
    get
    {
      lock (_owned)
      {
        return [.. _owned.Keys];
      }
    }
  }

  /// <summary>Re-attach (R3.1): swaps in the transport for the app's NEW connection.
  ///     The previous pump has ended (its transport closed with the dead connection);
  ///     the caller starts a fresh receive loop over the new transport. Waiters from
  ///     before the detach keep their sources — a re-emitted settle completes them.</summary>
  public void ReplaceTransport(NamedPipeChildTransport fresh)
  {
    ArgumentNullException.ThrowIfNull(fresh);
    NamedPipeChildTransport? stale = Interlocked.Exchange(ref _transport, fresh);
    if (stale is not null)
    {
      _ = Task.Run(async () => await stale.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
    }
  }

  /// <summary>Starts the host-side receive loop. Call once after connecting; every settle
  ///     envelope completes the matching WhenSettled await; connection loss completes every
  ///     waiter with a declared failure (FR-X3), never a hang.</summary>
  public Task RunReceiveLoopAsync(CancellationToken ct) => PumpAsync(ct);

  /// <inheritdoc cref="IAgentRuntime.Start"/>
  public async Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(record);
    TaskCompletionSource<AgentRunOutcome> source = new(TaskCreationOptions.RunContinuationsAsynchronously);
    _settling[record.Id.Value] = source;
    lock (_owned)
    {
      _owned[record.Id.Value] = 1;
    }

    StartCommand command = new(record.Id.Value, MaxConcurrent: 8, record.ModelUsed);
    return await SendStartAsync(command, record.Id, ct).ConfigureAwait(false);
  }

  /// <summary>Synchronous-by-contract delivery (the domain seam is sync): the underlying
  ///     wire send blocks only until the host's always-running pump consumes the frame,
  ///     which the pump model guarantees. Named decision: GetAwaiter().GetResult() over
  ///     .Wait() to avoid SyncCtx deadlocks in this ACL.</summary>
  public Result<bool> Deliver(AgentId id, PendingMessage message)
  {
    ArgumentNullException.ThrowIfNull(message);
    DeliverCommand command = new(id.Value, message.Text, (int)message.Urgency, message.Sender);
    bool sent = SendEnvelopeAsync("deliver", JsonSerializer.Serialize(command), id, CancellationToken.None)
        .GetAwaiter().GetResult();
    return sent
        ? Result.Success(true)
        : Result.Failure<bool>(new DomainError("HostUnavailable", "the child host refused the delivery."));
  }

  /// <inheritdoc cref="IAgentRuntime.WhenSettledAsync"/>
  public async Task<Result<AgentRunOutcome>> WhenSettledAsync(AgentId id, CancellationToken ct = default)
  {
    if (!_settling.TryGetValue(id.Value, out TaskCompletionSource<AgentRunOutcome>? source))
    {
      return Result.Failure<AgentRunOutcome>(new DomainError("NotFound",
          $"agent '{id}' has no live remote run."));
    }

    try
    {
      AgentRunOutcome outcome = ct.CanBeCanceled
          ? await source.Task.WaitAsync(ct).ConfigureAwait(false)
          : await source.Task.ConfigureAwait(false);
      return Result.Success(outcome);
    }
    catch (OperationCanceledException)
    {
      return Result.Failure<AgentRunOutcome>(new DomainError("Cancelled", "the wait was cancelled."));
    }
  }

  /// <inheritdoc cref="IAgentRuntime.InterruptSubtree"/>
  public void InterruptSubtree(AgentId rootOfSubtree)
      => _ = SendEnvelopeAsync("interrupt", JsonSerializer.Serialize(new InterruptCommand(rootOfSubtree.Value)), rootOfSubtree, CancellationToken.None);

  /// <inheritdoc cref="IAgentRuntime.Interrupt"/>
  public void Interrupt(AgentId? childId = null)
      => _ = SendEnvelopeAsync("interrupt", JsonSerializer.Serialize(new InterruptCommand(childId?.Value)), childId ?? new AgentId(Guid.NewGuid()), CancellationToken.None);

  private async Task PumpAsync(CancellationToken ct)
  {
    try
    {
      while (!ct.IsCancellationRequested)
      {
        NamedPipeChildTransport transport = _transport ?? throw new TransportClosedException("the runtime is not attached to a host pipe.");
        TransportEnvelope envelope = await transport.ReceiveAsync(ct).ConfigureAwait(false);
        if (envelope.Kind == "declare")
        {
          DeclareCommand? declared = JsonSerializer.Deserialize<DeclareCommand>(envelope.Json);
          if (declared is not null)
          {
            _declaredLive = [.. declared.LiveIds];
          }
        }
        else if (envelope.Kind == "error")
        {
          // Host-side fault for one child (e.g. record not found, host ceiling): complete
          // that child's waiters with a well-formed failure instead of leaving them hung.
          HostError? fault = JsonSerializer.Deserialize<HostError>(envelope.Json);
          if (fault is not null && _settling.TryRemove(fault.RecordId, out TaskCompletionSource<AgentRunOutcome>? faulted))
          {
            _ = faulted.TrySetResult(new AgentRunOutcome(new AgentId(fault.RecordId),
                AgentStatus.Failed, AgentFailureReason.ProviderError,
                "Error [HostFault]: " + fault.Message, "remote", 0));
          }
        }
        else if (envelope.Kind == "settle")
        {
          SettleNotice? notice = JsonSerializer.Deserialize<SettleNotice>(envelope.Json);
          if (notice is not null)
          {
            AgentStatus status = Enum.TryParse(notice.Status, out AgentStatus parsed)
                ? parsed : AgentStatus.Failed;
            AgentFailureReason? reason = Enum.TryParse(notice.Reason, out AgentFailureReason reasonParsed)
                ? reasonParsed : null;
            Settle(new AgentId(notice.RecordId), new AgentRunOutcome(new AgentId(notice.RecordId),
                status, reason, notice.Report, "remote", 0));
          }
        }

        await _transport.SendAsync(new TransportEnvelope("ack", "\"" + envelope.Sequence + "\"", envelope.Sequence), ct).ConfigureAwait(false);
      }
    }
    // Named decision (CA1031): the pump is the connection's fault boundary. Cancellation
    // ends it normally; any other failure completes every pending waiter with a declared
    // ProviderError — a well-formed outcome, never a hang (FR-X3).
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception)
    {
      foreach (TaskCompletionSource<AgentRunOutcome> source in _settling.Values)
      {
        _ = source.TrySetResult(new AgentRunOutcome(new AgentId(Guid.NewGuid()), AgentStatus.Failed,
            AgentFailureReason.ProviderError, "Error [ProviderError]: child host connection lost.", "remote", 0));
      }

      _settling.Clear();
    }
#pragma warning restore CA1031 // Do not catch general exception types
  }

  /// <summary>Completes every waiter for one child, RETAINING the completed source as
  ///     an outcome record — the same contract the in-process runtime's Settle upholds
  ///     (T3 ruling): a run may settle more than once (the host watchdog's wrap-up
  ///     retry settles the interrupted attempt before the FINAL terminal settle), and
  ///     a waiter observing the run at any point must read a well-formed outcome —
  ///     never NotFound, never a silently dropped envelope. The FIRST settle is the
  ///     interrupted attempt's outcome; later settles REPLACE it so late waiters see
  ///     the latest outcome.</summary>
  private void Settle(AgentId id, AgentRunOutcome outcome)
  {
    while (true)
    {
      if (_settling.TryGetValue(id.Value, out TaskCompletionSource<AgentRunOutcome>? source))
      {
        if (source.TrySetResult(outcome))
        {
          return;
        }

        // Stale completed source from an older run: late waiters must observe the
        // LATEST outcome, so swap in a fresh already-completed source and retry on
        // add/update races.
        TaskCompletionSource<AgentRunOutcome> replacement = NewSettleSource();
        _ = replacement.TrySetResult(outcome);
        if (_settling.TryUpdate(id.Value, replacement, source))
        {
          return;
        }
      }
      else
      {
        // Settle for an id this runtime never started (defensive, e.g. re-attach
        // delivers a settle for a child started by a previous app lifetime): record
        // the outcome so a late waiter reads a well-formed result instead of NotFound.
        TaskCompletionSource<AgentRunOutcome> fresh = NewSettleSource();
        _ = fresh.TrySetResult(outcome);
        _ = _settling.TryAdd(id.Value, fresh);
        return;
      }
    }
  }

  private static TaskCompletionSource<AgentRunOutcome> NewSettleSource()
      => new(TaskCreationOptions.RunContinuationsAsynchronously);

  private async Task<Result<AgentId>> SendStartAsync(StartCommand command, AgentId id, CancellationToken ct)
  {
    return await SendEnvelopeAsync("start", JsonSerializer.Serialize(command), id, ct).ConfigureAwait(false)
        ? Result.Success(id)
        : Result.Failure<AgentId>(new DomainError("HostUnavailable", "the child host refused the start."));
  }

  private async Task<bool> SendEnvelopeAsync(string kind, string json, AgentId id, CancellationToken ct)
  {
    long sequence = Interlocked.Increment(ref _sequence);
    try
    {
      NamedPipeChildTransport transport = _transport ?? throw new TransportClosedException("the runtime is not attached to a host pipe.");
      await transport.SendAsync(new TransportEnvelope(kind, json, sequence), ct).ConfigureAwait(false);
      return true;
    }
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception)
    {
      // Named decision (CA1031): a failed wire send is a declared HostUnavailable outcome.
      lock (_owned)
      {
        _ = _owned.TryRemove(id.Value, out _);
      }

      _ = _settling.TryRemove(id.Value, out _);
      return false;
    }
#pragma warning restore CA1031 // Do not catch general exception types
  }
}

public sealed record StartCommand(Guid RecordId, int MaxConcurrent, string ModelId);
public sealed record DeliverCommand(Guid RecordId, string Text, int Urgency, string Sender);
public sealed record InterruptCommand(Guid? RecordId);
public sealed record SettleNotice(Guid RecordId, string Status, string? Reason, string Report);
public sealed record DeclareCommand(IReadOnlyCollection<Guid> LiveIds);
public sealed record HostError(Guid RecordId, string Code, string Message);
