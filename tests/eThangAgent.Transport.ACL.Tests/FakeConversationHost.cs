using System.IO.Pipes;
using System.Text.Json;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>A minimal conversation host over a named pipe, standing in for the real
///     ChildHost executable so the app-side root-turn path is testable without it.
///     It deliberately does NOT reuse <see cref="RemoteAgentRuntime"/> — the production
///     client under test — so the test cannot pass by sharing the very code path it is
///     meant to verify. It speaks the wire directly through <see cref="TransportFraming"/>,
///     the same codec the production transport uses, so the byte shape is identical by
///     construction rather than by a second, drifting copy.
/// <para>The Input marker selects a behaviour: an ordinary input streams two deltas and
///     settles; <see cref="SilentInput"/> takes the turn and never speaks;
///     <see cref="SlowInput"/> streams a delta every <see cref="SlowDeltaInterval"/> for
///     <see cref="SlowDeltaCount"/> deltas then settles (a HEALTHY but long turn — the
///     progress-blind-budget probe); <see cref="ChattyInput"/> streams deltas forever so a
///     total-duration ceiling has something to cut; <see cref="HeartbeatInput"/> is QUIET
///     but alive — it emits a liveness heartbeat and settles (2026-10-22: the idle bound
///     must measure liveness, not busyness).</para></summary>
internal sealed class FakeConversationHost : IAsyncDisposable
{
  /// <summary>Takes the turn and never emits anything: the idle bound's subject.</summary>
  public const string SilentInput = "silent";

  /// <summary>A quiet but ALIVE turn: it emits a liveness heartbeat every
  ///     <see cref="HeartbeatInterval"/> and settles at the end. The idle bound's real
  ///     subject (2026-10-22) — a host that proves it is alive must never be failed as
  ///     unaddressable, however long the turn runs.</summary>
  public const string HeartbeatInput = "heartbeat";

  /// <summary>A genuinely healthy turn that simply takes a while: deltas keep arriving, so
  ///     no IDLE bound may fire — only a total-duration ceiling could.</summary>
  public const string SlowInput = "slow";

  /// <summary>Streams deltas forever and never settles: the hard ceiling's subject.</summary>
  public const string ChattyInput = "chatty";

  /// <summary>How often <see cref="SlowInput"/> emits a delta.</summary>
  public static readonly TimeSpan SlowDeltaInterval = TimeSpan.FromMilliseconds(400);

  /// <summary>How often <see cref="HeartbeatInput"/> emits its liveness heartbeat.</summary>
  public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(150);

  /// <summary>How many heartbeats <see cref="HeartbeatInput"/> emits before settling.</summary>
  public const int HeartbeatCount = 10;

  /// <summary>How many deltas <see cref="SlowInput"/> emits before settling.</summary>
  public const int SlowDeltaCount = 6;

  private readonly NamedPipeServerStream _server;
  private readonly CancellationTokenSource _cts = new();
  private readonly Task _pump;
  private readonly List<RootTurnRequest> _received = [];
  private readonly List<string> _interruptedTurnIds = [];
  private readonly List<(string TurnId, int Ordinal, long Generation)> _held = [];
  private readonly Lock _gate = new();
  private readonly SemaphoreSlim _writeGate = new(1, 1);

  private FakeConversationHost(NamedPipeServerStream server, string pipeName)
  {
    _server = server;
    PipeName = pipeName;
    _pump = Task.Run(ServeAsync, _cts.Token);
  }

  /// <summary>The pipe the app-side client connects to.</summary>
  public string PipeName { get; }

  /// <summary>The turns the host actually decoded off the wire — the proof that the
  ///     client's envelope really carried the request, not that a local shortcut did.</summary>
  public IReadOnlyList<RootTurnRequest> Received
  {
    get
    {
      lock (_gate)
      {
        return [.. _received];
      }
    }
  }

  /// <summary>The turn ids the app asked the host to STOP, in arrival order. The
  ///     evidence that an abandoned turn is released on the host side (2026-10-22): the
  ///     host holds its turn gate for the whole turn, so a turn the app gave up on would
  ///     otherwise block every later turn in the session.</summary>
  public IReadOnlyList<string> InterruptedTurnIds
  {
    get
    {
      lock (_gate)
      {
        return [.. _interruptedTurnIds];
      }
    }
  }

  public static FakeConversationHost Start()
  {
    string pipeName = "ethang-root-" + Guid.NewGuid().ToString("N");
    NamedPipeServerStream server = new(pipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    return new FakeConversationHost(server, pipeName);
  }

  /// <summary>Settles every turn the host is holding, IN ARRIVAL ORDER, each with a payload
  ///     naming its own ordinal ("released#1", "released#2", ...). The ordinal is what makes
  ///     a STALE settle distinguishable from a fresh one when two turns share a turn id.</summary>
  public async Task ReleaseHeldTurnsAsync()
  {
    (string TurnId, int Ordinal, long Generation)[] held;
    lock (_gate)
    {
      held = [.. _held];
      _held.Clear();
    }

    foreach ((string turnId, int ordinal, long generation) in held)
    {
      await EmitAsync(new RootTurnEvent(turnId, "settled", "released#" + ordinal, generation))
          .ConfigureAwait(false);
    }
  }

  private async Task ServeAsync()
  {
    try
    {
      await _server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
      while (!_cts.IsCancellationRequested)
      {
        TransportEnvelope envelope = await TransportFraming.ReadAsync(_server, _cts.Token).ConfigureAwait(false);
        if (envelope.Kind == "interrupt")
        {
          RecordInterrupt(envelope.Json);
          continue;
        }

        if (envelope.Kind != "root-turn")
        {
          continue; // acks and anything else the app sends are not this host's business
        }

        RootTurnRequest request = RootTurnProtocol.ParseRequest(envelope.Json);
        lock (_gate)
        {
          _received.Add(request);
          if (string.Equals(request.Input, SilentInput, StringComparison.Ordinal))
          {
            _held.Add((request.TurnId, _received.Count, request.Generation));
          }
        }

        // Each turn streams on its OWN task so a long or endless turn never blocks the
        // serve loop from reading the next request (a real host behaves the same way).
        _ = Task.Run(() => RunTurnAsync(request), _cts.Token);
      }
    }
    // Named decision (CA1031): the fake host's serve loop is its fault boundary — a
    // closed pipe or a cancelled token IS the test's teardown, never a test failure.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception)
    {
      // expected teardown: the app went away or the test stopped the host
    }
#pragma warning restore CA1031 // Do not catch general exception types
  }

  /// <summary>Records the turn id an interrupt envelope asked the host to stop.</summary>
  private void RecordInterrupt(string json)
  {
    InterruptCommand? command = JsonSerializer.Deserialize<InterruptCommand>(json);
    if (command?.TurnId is { Length: > 0 } turnId)
    {
      lock (_gate)
      {
        _interruptedTurnIds.Add(turnId);
      }
    }
  }

  /// <summary>Streams one turn according to its Input marker.</summary>
  private async Task RunTurnAsync(RootTurnRequest request)
  {
    if (string.Equals(request.Input, SilentInput, StringComparison.Ordinal))
    {
      return; // takes the turn, never settles it until released: the waiter stays open
    }

    if (string.Equals(request.Input, HeartbeatInput, StringComparison.Ordinal))
    {
      // Quiet, but alive: the heartbeat is the proof of life the app's idle bound needs.
      for (int i = 0; i < HeartbeatCount; i++)
      {
        await Task.Delay(HeartbeatInterval, _cts.Token).ConfigureAwait(false);
        await EmitAsync(request, "progress", "alive-" + i).ConfigureAwait(false);
      }

      await EmitAsync(request, "settled", "heartbeat-done").ConfigureAwait(false);
      return;
    }

    if (string.Equals(request.Input, ChattyInput, StringComparison.Ordinal))
    {
      for (int i = 0; i < int.MaxValue && !_cts.IsCancellationRequested; i++)
      {
        await EmitAsync(request, "delta", "tick-" + i).ConfigureAwait(false);
        await Task.Delay(SlowDeltaInterval, _cts.Token).ConfigureAwait(false);
      }

      return;
    }

    if (string.Equals(request.Input, SlowInput, StringComparison.Ordinal))
    {
      for (int i = 0; i < SlowDeltaCount; i++)
      {
        await Task.Delay(SlowDeltaInterval, _cts.Token).ConfigureAwait(false);
        await EmitAsync(request, "delta", "slow-" + i).ConfigureAwait(false);
      }

      await EmitAsync(request, "settled", "slow-done").ConfigureAwait(false);
      return;
    }

    foreach (string chunk in new[] { "he", "llo" })
    {
      await EmitAsync(request, "delta", chunk).ConfigureAwait(false);
    }

    await EmitAsync(request, "settled", "done").ConfigureAwait(false);
  }

  private Task EmitAsync(RootTurnRequest request, string kind, string payload)
      => EmitAsync(new RootTurnEvent(request.TurnId, kind, payload, request.Generation));

  /// <summary>Writes one root-turn event. Serialized against the serve loop's own writes:
  ///     two writers on one pipe would interleave frames.</summary>
  private async Task EmitAsync(RootTurnEvent evt)
  {
    await _writeGate.WaitAsync(_cts.Token).ConfigureAwait(false);
    try
    {
      await TransportFraming.WriteAsync(_server,
          new TransportEnvelope("root-turn-event", RootTurnProtocol.SerializeEvent(evt), 0), _cts.Token)
          .ConfigureAwait(false);
    }
    finally
    {
      _ = _writeGate.Release();
    }
  }

  /// <summary>Simulates host death: the pipe closes, so the app-side runtime observes a
  ///     disconnected transport instead of a peer that simply stopped talking.</summary>
  public async Task StopAsync()
  {
    await _cts.CancelAsync().ConfigureAwait(false);
    await _server.DisposeAsync().ConfigureAwait(false);
  }

  public async ValueTask DisposeAsync()
  {
    await StopAsync().ConfigureAwait(false);
    try
    {
      await _pump.ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is OperationCanceledException or IOException)
    {
      // expected teardown
    }

    _writeGate.Dispose();
    _cts.Dispose();
  }
}
