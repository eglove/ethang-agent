using System.IO.Pipes;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>A minimal conversation host over a named pipe, standing in for the real
///     ChildHost executable so the app-side root-turn path is testable without it.
///     It deliberately does NOT reuse <see cref="RemoteAgentRuntime"/> — the production
///     client under test — so the test cannot pass by sharing the very code path it is
///     meant to verify. It speaks the wire directly through <see cref="TransportFraming"/>,
///     the same codec the production transport uses, so the byte shape is identical by
///     construction rather than by a second, drifting copy.</summary>
internal sealed class FakeConversationHost : IAsyncDisposable
{
  /// <summary>The input marker that makes the host TAKE a turn and never settle it —
  ///     the connection-loss test's way of holding a waiter open.</summary>
  public const string SilentInput = "silent";

  private readonly NamedPipeServerStream _server;
  private readonly CancellationTokenSource _cts = new();
  private readonly Task _pump;
  private readonly List<RootTurnRequest> _received = [];
  private readonly Lock _gate = new();

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

  public static FakeConversationHost Start()
  {
    string pipeName = "ethang-root-" + Guid.NewGuid().ToString("N");
    NamedPipeServerStream server = new(pipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    return new FakeConversationHost(server, pipeName);
  }

  private async Task ServeAsync()
  {
    try
    {
      await _server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
      while (!_cts.IsCancellationRequested)
      {
        TransportEnvelope envelope = await TransportFraming.ReadAsync(_server, _cts.Token).ConfigureAwait(false);
        if (envelope.Kind != "root-turn")
        {
          continue; // acks and anything else the app sends are not this host's business
        }

        RootTurnRequest request = RootTurnProtocol.ParseRequest(envelope.Json);
        lock (_gate)
        {
          _received.Add(request);
        }

        if (string.Equals(request.Input, SilentInput, StringComparison.Ordinal))
        {
          continue; // takes the turn, never settles it: the waiter stays open
        }

        foreach (string chunk in new[] { "he", "llo" })
        {
          await EmitAsync(new RootTurnEvent(request.TurnId, "delta", chunk)).ConfigureAwait(false);
        }

        await EmitAsync(new RootTurnEvent(request.TurnId, "settled", "done")).ConfigureAwait(false);
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

  private Task EmitAsync(RootTurnEvent evt)
      => TransportFraming.WriteAsync(_server,
          new TransportEnvelope("root-turn-event", RootTurnProtocol.SerializeEvent(evt), 0), _cts.Token);

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

    _cts.Dispose();
  }
}
