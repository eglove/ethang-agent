using eThangAgent.SharedKernel;
using System.Collections.Concurrent;
using System.Text.Json;
using eThangAgent.AgentDomain;
using eThangAgent.Transport.ACL;

namespace eThangAgent.ChildHost;

/// <summary>The host's serve loop: receives envelopes, runs children through the real
///     spawner stack built from a headless session composition, streams events/settlements
///     back, acks app-sent frames (declared at-least-once, FR-X3), and enforces the host's
///     own concurrency ceiling (FR-X5). One connection; host exits when the app disconnects
///     or the pipe closes — children keep running per survivability (FR-L7/T4) and the
///     app re-attaches on restart.</summary>
public sealed class ChildHostServer(NamedPipeChildTransport transport, string settingsPath, string databasePath)
{
  private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _active = [];
  private long _sequence;

  public async Task ServeAsync()
  {
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

      await transport.SendAsync(new TransportEnvelope("ack", "\"" + envelope.Sequence + "\"", envelope.Sequence)).ConfigureAwait(false);

      switch (envelope.Kind)
      {
        case "start":
          await HandleStartAsync(envelope).ConfigureAwait(false);
          break;
        case "interrupt":
          HandleInterrupt(envelope);
          break;
        default:
          break; // unknown kinds ignored forward-compatibly
      }
    }
  }

  private async System.Threading.Tasks.Task HandleStartAsync(TransportEnvelope envelope)
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
    _ = System.Threading.Tasks.Task.Run(() => RunChildAsync(command, cts));
  }

  private async System.Threading.Tasks.Task RunChildAsync(StartCommand command, CancellationTokenSource cts)
  {
    try
    {
      SessionHost host = SessionHost.Create(settingsPath, databasePath);
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

  private async System.Threading.Tasks.Task SendAsync(string kind, string json)
  {
    long sequence = System.Threading.Interlocked.Increment(ref _sequence);
    await transport.SendAsync(new TransportEnvelope(kind, json, sequence)).ConfigureAwait(false);
  }
}

public sealed record StartCommand(Guid RecordId, int MaxConcurrent, string ModelId);
public sealed record InterruptCommand(Guid? RecordId);
public sealed record SettleNotice(Guid RecordId, string Status, string? Reason, string Report);
public sealed record HostError(Guid RecordId, string Code, string Message);
