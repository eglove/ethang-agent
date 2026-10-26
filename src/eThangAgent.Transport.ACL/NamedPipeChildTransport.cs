using System.IO.Pipes;
using System.Threading.Channels;

namespace eThangAgent.Transport.ACL;

/// <summary>Named-pipe transport between the app and the supervised child host (approved
///     hosting decision: ONE long-lived host process, one connection). Server side = the
///     host; client side = the app. Windows-only by repo doctrine.
///
///     IO model: named-pipe writes BLOCK until the peer consumes, so the transport owns a
///     continuous READ PUMP per direction — every received frame lands in a Channel and
///     ReceiveAsync drains it; SendAsync frames are written by a single writer pump. A write
///     therefore always has a consuming read pending on the peer (whose own pump is always
///     reading), and no send can deadlock against an absent reader. Framing rides
///     <see cref="TransportFraming"/>; delivery is at-least-once by envelope Sequence.</summary>
public sealed class NamedPipeChildTransport : IChildTransport, IAsyncDisposable
{
  // A client that connected and vanished before ConnectNamedPipe was issued: ERROR_NO_DATA
  // (232) is the documented outcome, ERROR_PIPE_NOT_CONNECTED (233) the same condition a step
  // later. Both mean 'nobody to serve', never 'the connection this host serves was lost'.
  private const int NoDataHResult = unchecked((int)0x800700E8);
  private const int PipeNotConnectedHResult = unchecked((int)0x800700E9);

  private readonly Stream _stream;
  private readonly Channel<TransportEnvelope> _inbox = Channel.CreateUnbounded<TransportEnvelope>(
      new UnboundedChannelOptions { SingleReader = true });
  private readonly Task _pump;
  private readonly SemaphoreSlim _writeGate = new(1, 1);
  private volatile bool _closed;

  private NamedPipeChildTransport(Stream stream)
  {
    _stream = stream;
    _pump = PumpAsync(stream);
  }

  /// <summary>Host side: creates the pipe and waits for the app to connect.
  ///     <para>RE-ARMS ON A DISTURBED ACCEPT. The listening instance exists from this
  ///     constructor, but ConnectNamedPipe is only issued by WaitForConnectionAsync — so a
  ///     client that connects and goes away inside that window leaves nothing to accept and
  ///     the wait fails with ERROR_NO_DATA ("The pipe is being closed"). That is NOT the
  ///     connection this host serves being lost: it is a client that was never served.
  ///     Treating it as fatal killed the host on an ordinary app-side connect-then-relaunch,
  ///     and a host that dies here is indistinguishable from one that never started.
  ///     The poisoned instance cannot be re-armed (measured: a second WaitForConnectionAsync
  ///     on it throws the same error), so a FRESH instance on the same name is created and
  ///     the wait resumes — that is the instance the next client connects to. Waiting stays
  ///     unbounded exactly as before: the app's supervisor owns the deadline.</para></summary>
  public static Task<NamedPipeChildTransport> AcceptAppAsync(string pipeName, CancellationToken ct = default)
      => AcceptAppAsync(pipeName, disturbForTest: null, ct);

  /// <summary>The accept, with an integration-test seam (InternalsVisibleTo only):
  ///     <paramref name="disturbForTest"/> runs AFTER the listening instance exists and
  ///     BEFORE ConnectNamedPipe is issued, which is the exact window the disturbed accept
  ///     lives in. Production passes null, so the seam costs one null check and changes no
  ///     behavior. It exists because the window cannot be hit on demand from outside:
  ///     measured 2026-10-26, a client that dials before the accept is armed is simply
  ///     SERVED (0/40 disturbed) — the race only opens under real scheduling contention, so
  ///     a test that waited for it would be a flake, not a regression test.</summary>
  internal static async Task<NamedPipeChildTransport> AcceptAppAsync(string pipeName,
      Func<Task>? disturbForTest, CancellationToken ct = default)
  {
    while (true)
    {
      NamedPipeServerStream server = new(pipeName, PipeDirection.InOut, 1,
          PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
      try
      {
        if (disturbForTest is not null)
        {
          await disturbForTest().ConfigureAwait(false);
        }

        await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
        return new NamedPipeChildTransport(server);
      }
      catch (IOException ex) when (ex.HResult is NoDataHResult or PipeNotConnectedHResult)
      {
        // A client connected and left before the accept was issued: nobody to serve.
        await Console.Error.WriteLineAsync("accept-disturbed: " + ex.Message + "; re-arming").ConfigureAwait(false);
        await server.DisposeAsync().ConfigureAwait(false);
      }
      // Named decision (CA1031): the pump is the transport's fault boundary — ANY failure here
      // means the connection is gone and must surface as the declared closed condition.
#pragma warning disable CA1031 // Do not catch general exception types
      catch
      {
        await server.DisposeAsync().ConfigureAwait(false);
        throw;
      }
    }
  }

  /// <summary>App side: connects to the host's pipe, failing fast when absent — a missing
  ///     host is a declared condition, never a hang.</summary>
  public static async Task<NamedPipeChildTransport> ConnectToHostAsync(string pipeName, CancellationToken ct = default)
  {
    NamedPipeClientStream client = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    try
    {
      await client.ConnectAsync(ct).ConfigureAwait(false);
      return new NamedPipeChildTransport(client);
    }
    // Named decision (CA1031): the pump is the transport's fault boundary — ANY failure here
    // means the connection is gone and must surface as the declared closed condition.
#pragma warning disable CA1031 // Do not catch general exception types
    catch
    {
      await client.DisposeAsync().ConfigureAwait(false);
      throw;
    }
  }

  public Task ConnectAsync(CancellationToken ct = default)
      => Task.CompletedTask; // connection happened at the factory

  public async Task SendAsync(TransportEnvelope envelope, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(envelope);
    ObjectDisposedException.ThrowIf(_closed, this);
    // Serialized writes: the pump model needs one writer at a time on the single pipe.
    await _writeGate.WaitAsync(ct).ConfigureAwait(false);
    try
    {
      await TransportFraming.WriteAsync(_stream, envelope, ct).ConfigureAwait(false);
    }
    finally
    {
      _ = _writeGate.Release();
    }
  }

  /// <summary>Drains the next received envelope. Fails with the declared
  ///     TransportClosedException when the pipe closed (pump completed).</summary>
  public async Task<TransportEnvelope> ReceiveAsync(CancellationToken ct = default)
  {
    ObjectDisposedException.ThrowIf(_closed, this);
    Task<TransportEnvelope> read = _inbox.Reader.ReadAsync(ct).AsTask();
    Task winner = await Task.WhenAny(read, _pump).ConfigureAwait(false);
    if (winner != read)
    {
      throw new TransportClosedException("the pipe closed while awaiting a frame.");
    }

    try
    {
      return await read.ConfigureAwait(false);
    }
    catch (ChannelClosedException ex)
    {
      // The pump completed between WhenAny and the read: same declared condition.
      throw new TransportClosedException("the pipe closed while awaiting a frame.", ex);
    }
  }

  /// <summary>The read pump: continuously framing-reads so the peer's writes always find a
  ///     consumer (the named-pipe write-block rule). Faults close the inbox — ReceiveAsync
  ///     then surfaces the declared closed condition.</summary>
  private async Task PumpAsync(Stream stream)
  {
    try
    {
      while (!_closed)
      {
        TransportEnvelope envelope = await TransportFraming.ReadAsync(stream).ConfigureAwait(false);
        await _inbox.Writer.WriteAsync(envelope, CancellationToken.None).ConfigureAwait(false);
      }
    }
    // Named decision (CA1031): the pump is the transport's fault boundary — ANY failure here
    // means the connection is gone and must surface as the declared closed condition.
#pragma warning disable CA1031 // Do not catch general exception types
    catch
    {
      // Declared failure path: any IO/framing fault means the connection is gone.
    }
#pragma warning restore CA1031 // Do not catch general exception types
    finally
    {
      _ = _inbox.Writer.TryComplete();
    }
  }

  public async ValueTask DisposeAsync()
  {
    if (_closed)
    {
      return;
    }

    _closed = true;
    // A server pipe MUST Disconnect before Dispose: without it the peer's pending read
    // never observes EOF and blocks forever. Disconnect gives the peer a clean pipe-closed
    // signal, which its pump surfaces as the declared closed condition.
    //
    // GUARDED (issue #274): when the PEER went away first - now the ordinary way a host
    // connection ends - Disconnect throws IOException because there is nothing left to
    // disconnect. That is the desired end state, not a failure: unguarded, it escaped the
    // host's top-level entry point as an unhandled exception and the process died on a
    // crash path instead of exiting cleanly.
    if (_stream is NamedPipeServerStream server)
    {
#pragma warning disable CA1031 // Named decision: a peer that already left is the desired end state.
      try
      {
        server.Disconnect();
      }
      catch (IOException)
      {
        // The peer is already gone: exactly what this method is trying to achieve.
      }
      catch (ObjectDisposedException)
      {
        // The pipe was already torn down.
      }
#pragma warning restore CA1031
    }

    await _stream.DisposeAsync().ConfigureAwait(false);
    try
    {
      await _pump.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }
#pragma warning disable CA1031 // Do not catch general exception types
    catch
    {
      // Named decision (CA1031): pump teardown is best-effort; the pipe is already disposed.
    }
#pragma warning restore CA1031 // Do not catch general exception types
    _writeGate.Dispose();
  }
}
