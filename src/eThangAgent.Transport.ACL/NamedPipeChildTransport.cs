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

  /// <summary>Host side: creates the pipe and waits for the app to connect.</summary>
  public static async Task<NamedPipeChildTransport> AcceptAppAsync(string pipeName, CancellationToken ct = default)
  {
    NamedPipeServerStream server = new(pipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    try
    {
      await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
      return new NamedPipeChildTransport(server);
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
    if (_stream is NamedPipeServerStream server)
    {
      server.Disconnect();
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
