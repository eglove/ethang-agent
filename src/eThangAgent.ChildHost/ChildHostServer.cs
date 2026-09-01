using System.Collections.Concurrent;
using System.Text.Json;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Transport.ACL;

namespace eThangAgent.ChildHost;

/// <summary>The host's serve loop: receives envelopes, runs children through the real
///     spawner stack built from a headless session composition, streams events/settlements
///     back, acks app-sent frames (declared at-least-once, FR-X3), and enforces the host's
///     own concurrency ceiling (FR-X5). One connection; host exits when the app disconnects
///     or the pipe closes — children keep running per survivability (FR-L7/T4) and the
///     app re-attaches on restart.</summary>
public sealed class ChildHostServer(string settingsPath, string databasePath)
{
  private volatile NamedPipeChildTransport? _transport;
  private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _active = [];
  private readonly ConcurrentDictionary<Guid, BoundedAgentMailbox> _mailboxes = [];
  private long _sequence;

  /// <summary>The ids the host currently runs — the exact live set the app's orphan
  ///     repair consumes (FR-L8). Sent on every (re)connect and after each mutation.</summary>
  public IReadOnlyCollection<Guid> LiveIds() => [.. _active.Keys];

  /// <summary>Serves the CURRENT app connection. On entry the host declares its live
  ///     set (R3.1: a re-attaching app learns exactly which Running records the host
  ///     still owns). When the app goes away children keep running (survivability); a
  ///     later call with a fresh transport re-attaches them and settle emission
  ///     resumes through <see cref="AttachTransport"/>.</summary>
  public async Task ServeAsync()
  {
    // The caller attaches the transport BEFORE calling ServeAsync; hold it locally so
    // the serve loop reads one consistent connection even if a new one attaches later.
    NamedPipeChildTransport transport = _transport
        ?? throw new InvalidOperationException("AttachTransport must be called before ServeAsync.");
    await SendLiveSetAsync().ConfigureAwait(false);
    while (true)
    {
      TransportEnvelope envelope;
      try
      {
        envelope = await transport.ReceiveAsync().ConfigureAwait(false);
      }
      catch (TransportClosedException)
      {
        return; // app gone: children keep running (survivability)
      }

      // Named decision (CA1031): a failed ACK write IS the disconnect (broken pipe) —
      // return so the accept loop re-attaches the next connection; children keep running.
#pragma warning disable CA1031 // Do not catch general exception types
      try
      {
        await transport.SendAsync(new TransportEnvelope("ack", "\"" + envelope.Sequence + "\"", envelope.Sequence)).ConfigureAwait(false);
      }
      catch
      {
        return;
      }
#pragma warning restore CA1031

      switch (envelope.Kind)
      {
        case "start":
          await HandleStartAsync(envelope).ConfigureAwait(false);
          break;
        case "deliver":
          HandleDeliver(envelope);
          break;
        case "interrupt":
          HandleInterrupt(envelope);
          break;
        default:
          break; // unknown kinds ignored forward-compatibly
      }
    }
  }

  private async Task HandleStartAsync(TransportEnvelope envelope)
  {
    StartCommand command = JsonSerializer.Deserialize<StartCommand>(envelope.Json)
        ?? throw new InvalidOperationException("null start command.");
    if (_active.Count >= command.MaxConcurrent)
    {
      // Host-boundary budget enforcement (FR-X5): refuse rather than oversubscribe.
      await SendAsync("error", JsonSerializer.Serialize(new HostError(command.RecordId, "ConcurrencyCapReached",
          "the child host is at its concurrency ceiling."))).ConfigureAwait(false);
      return;
    }

    CancellationTokenSource cts = new();
    _active[command.RecordId] = cts;
    _mailboxes[command.RecordId] = new BoundedAgentMailbox();
    _ = Task.Run(() => RunChildAsync(command, cts));
    // Named decision (CA1031): a failed live-set broadcast IS the disconnect; the accept
    // loop re-attaches. The child was already registered and keeps running.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      await SendLiveSetAsync().ConfigureAwait(false);
    }
    catch
    {
      // Swallowed deliberately: see the named decision above.
    }
#pragma warning restore CA1031
  }

  private async Task RunChildAsync(StartCommand command, CancellationTokenSource cts)
  {
    try
    {
      SessionHost host = SessionHost.Create(settingsPath, databasePath,
        inboxFor: id => _mailboxes.TryGetValue(id.Value, out BoundedAgentMailbox? mailbox) ? mailbox : null);
      Result<AgentRecord> loaded = await host.Store.GetAsync(new AgentId(command.RecordId), CancellationToken.None).ConfigureAwait(false);
      if (!loaded.IsSuccess)
      {
        await SendAsync("error", JsonSerializer.Serialize(new HostError(command.RecordId, "NotFound",
            "record not found in the shared database."))).ConfigureAwait(false);
        return;
      }

      AgentRunOutcome outcome = await host.Spawner.RunAsync(loaded.Value, cts.Token).ConfigureAwait(false); // S8949 named: token travels to the run
      await SendAsync("settle", JsonSerializer.Serialize(new SettleNotice(command.RecordId,
          outcome.Status.ToString(), outcome.Reason?.ToString(), outcome.Report))).ConfigureAwait(false);
    }
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception ex)
    {
      // Named decision (CA1031): the host is an isolation boundary — ANY child fault becomes
      // a well-formed error envelope to the app, never a host crash.
      await SendAsync("error", JsonSerializer.Serialize(new HostError(command.RecordId, "HostFault", ex.Message))).ConfigureAwait(false);
    }
#pragma warning restore CA1031 // Do not catch general exception types
    finally
    {
      _ = _active.TryRemove(command.RecordId, out _);
      _ = _mailboxes.TryRemove(command.RecordId, out _);
      await SendLiveSetAsync().ConfigureAwait(false);
    }
  }

  /// <summary>Delivers a steering envelope into the running child's mailbox (FR-C2).
  ///     NotRunning (unknown/finished child) and MailboxFull are dropped here: the
  ///     APP-side runtime already returned those receipts to the sender synchronously —
  ///     the wire path is the at-least-once replay, and a replay after a settle is
  ///     stale by definition (A3: no silent drops on the live path).</summary>
  private void HandleDeliver(TransportEnvelope envelope)
  {
    DeliverCommand? command = JsonSerializer.Deserialize<DeliverCommand>(envelope.Json);
    if (command is null)
    {
      return;
    }

    if (_mailboxes.TryGetValue(command.RecordId, out BoundedAgentMailbox? mailbox))
    {
      _ = mailbox.Deliver(new PendingMessage(command.Text, (MessageUrgency)command.Urgency,
          DateTimeOffset.UtcNow, command.Sender));
    }
  }

  private void HandleInterrupt(TransportEnvelope envelope)
  {
    InterruptCommand? command = JsonSerializer.Deserialize<InterruptCommand>(envelope.Json);
    if (command!.RecordId is { } id)
    {
      if (_active.TryGetValue(id, out CancellationTokenSource? cts))
      {
        cts.Cancel();
      }
    }
    else
    {
      foreach (CancellationTokenSource cts in _active.Values)
      {
        cts.Cancel();
      }
    }
  }

  /// <summary>Declares the host's live child set to the app (R3.2's exact orphan
  ///     resolution input). Called on connect and after every start/settle mutation.</summary>
  private async Task SendLiveSetAsync()
      => await SendAsync("declare", JsonSerializer.Serialize(new DeclareCommand([.. LiveIds()]))).ConfigureAwait(false);

  /// <summary>Re-attach (R3.1): swaps in the app's NEW connection so settles for
  ///     children that kept running during the app's absence reach it again.
  ///     Single-connection by design; the old transport is closed first.</summary>
  public void AttachTransport(NamedPipeChildTransport fresh)
  {
    NamedPipeChildTransport? stale = Interlocked.Exchange(ref _transport, fresh);
    if (stale is not null)
    {
      _ = Task.Run(async () => await stale.DisposeAsync().ConfigureAwait(false));
    }
  }

  private async Task SendAsync(string kind, string json)
  {
    NamedPipeChildTransport? transport = _transport;
    if (transport is null)
    {
      return; // no app attached (yet): settles/declares resume on the next attach
    }

    long sequence = Interlocked.Increment(ref _sequence);
    await transport.SendAsync(new TransportEnvelope(kind, json, sequence)).ConfigureAwait(false);
  }
}

public sealed record HostError(Guid RecordId, string Code, string Message);
