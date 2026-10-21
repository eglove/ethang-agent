using System.IO.Pipes;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>A peer that ACCEPTS the connection and then either never reads a byte, reads
///     far too slowly, or reads ONE FRAME HEADER and stops — the "host accepted but is not
///     keeping up" probes. A named-pipe write blocks once the peer's buffer fills, so a large
///     enough send against this peer does not complete in time. Exists to prove the app-side
///     send path is bounded rather than relying on the peer's manners.
/// <para>Three modes, because they are three DIFFERENT failures and the code must tell them
///     apart. A peer that reads NOTHING and a peer draining 4KB every 50ms are both
///     recoverable: the write is cancelled before any byte of the frame reaches the wire, the
///     stream stays intact, and the connection survives. A peer that reads the 4-byte frame
///     HEADER and then stops is NOT recoverable: the header is on the wire and the payload can
///     never follow, so its frame stream is permanently desynchronized (the re-reviewer's
///     measured desync). Only <see cref="StartStallingAfterHeaderAsync"/> can distinguish a
///     cancelled write from an abandoned one.</para></summary>
internal sealed class NonReadingPeer : IAsyncDisposable
{
  private readonly NamedPipeServerStream _server;
  private readonly Task _accept;
  private readonly CancellationTokenSource _cts = new();

  private NonReadingPeer(NamedPipeServerStream server, string pipeName, Task accept, PeerMode mode,
      int completeFrames)
  {
    _server = server;
    PipeName = pipeName;
    _accept = accept;
    if (mode is PeerMode.SlowReader)
    {
      _ = Task.Run(async () =>
      {
        await accept.ConfigureAwait(false);
        await DrainSlowlyAsync(server).ConfigureAwait(false);
      }, _cts.Token);
    }

    if (mode is PeerMode.StallAfterHeader)
    {
      _ = Task.Run(async () =>
      {
        await accept.ConfigureAwait(false);
        await ReadWholeFramesThenStallAsync(server, completeFrames).ConfigureAwait(false);
      }, _cts.Token);
    }
  }

  /// <summary>How the peer consumes the stream.</summary>
  private enum PeerMode
  {
    /// <summary>Reads nothing at all.</summary>
    NeverReads,

    /// <summary>Reads, but only 4KB every 50ms.</summary>
    SlowReader,

    /// <summary>Reads exactly one 4-byte frame header, then never reads again.</summary>
    StallAfterHeader,
  }

  public string PipeName { get; }

  /// <summary>True once the peer has consumed the 4-byte frame header it is stalling on —
  ///     the moment a mid-frame cancellation becomes unrecoverable. Only meaningful in
  ///     <see cref="StartStallingAfterHeaderAsync"/> mode.</summary>
  public bool HeaderConsumed => Volatile.Read(ref _headerConsumed) == 1;

  private int _headerConsumed;

  /// <summary>Creates the pipe and returns AT ONCE; the connection is awaited in the
  ///     background. Awaiting it here would deadlock: the peer would wait for a client that
  ///     the caller only connects after this returns.</summary>
  public static Task<NonReadingPeer> StartAsync()
      => StartCoreAsync(PeerMode.NeverReads);

  /// <summary>A peer that DOES read, just far slower than the writer produces (4KB every
  ///     50ms). The distinction matters: this peer is reading, so the failure must not claim
  ///     it is not.</summary>
  public static Task<NonReadingPeer> StartSlowReaderAsync()
      => StartCoreAsync(PeerMode.SlowReader);

  /// <summary>A peer that consumes <paramref name="completeFrames"/> WHOLE frames and then
  ///     reads only the NEXT frame's 4-byte header before stopping forever.
  ///     This is the desync probe: the writer's header is on the wire and its payload can
  ///     never follow, so the peer can never parse another frame and every later frame the
  ///     writer sends is swallowed as that frame's payload. A write against this peer can
  ///     NEVER drain to completion, which is what makes it able to tell a CANCELLED write
  ///     from an ABANDONED one — an abandoned write keeps the gate forever, and the
  ///     connection stays silently dead instead of being torn down.</summary>
  public static Task<NonReadingPeer> StartStallingAfterHeaderAsync(int completeFrames)
      => StartCoreAsync(PeerMode.StallAfterHeader, completeFrames);

  private static Task<NonReadingPeer> StartCoreAsync(PeerMode mode, int completeFrames = 0)
  {
    string prefix = mode switch
    {
      PeerMode.NeverReads => "ethang-noread-",
      PeerMode.SlowReader => "ethang-slowread-",
      PeerMode.StallAfterHeader => "ethang-stallheader-",
      _ => throw new InvalidOperationException("unknown peer mode " + mode + "."),
    };
    string pipeName = prefix + Guid.NewGuid().ToString("N");
    NamedPipeServerStream server = new(pipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    NonReadingPeer peer = new(server, pipeName,
        Task.Run(() => server.WaitForConnectionAsync(TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken),
        mode, completeFrames);
    return Task.FromResult(peer);
  }

  /// <summary>Consumes the given number of WHOLE frames through the production codec, then
  ///     reads exactly one 4-byte header, records that it did, and never reads again —
  ///     leaving the peer's frame stream permanently desynchronized.</summary>
  private async Task ReadWholeFramesThenStallAsync(NamedPipeServerStream server, int completeFrames)
  {
    try
    {
      for (int i = 0; i < completeFrames; i++)
      {
        _ = await TransportFraming.ReadAsync(server, _cts.Token).ConfigureAwait(false);
      }

      byte[] header = new byte[4];
      int read = 0;
      while (read < 4)
      {
        int chunk = await server.ReadAsync(header.AsMemory(read, 4 - read), _cts.Token).ConfigureAwait(false);
        if (chunk == 0)
        {
          return; // peer closed
        }

        read += chunk;
      }

      Volatile.Write(ref _headerConsumed, 1);
    }
    catch (Exception ex) when (ex is OperationCanceledException or IOException)
    {
      // expected teardown
    }
  }

  /// <summary>Starts draining the pipe as fast as it can. A peer that never reads leaves the
  ///     pipe buffer FULL, so even a tiny write blocks no matter what the write gate is
  ///     doing — which makes "is the gate free?" untestable against it. Draining first is
  ///     what isolates the gate from the buffer.</summary>
  public void StartDraining()
  {
    _ = Task.Run(async () =>
    {
      byte[] buffer = new byte[65536];
      try
      {
        while (!_cts.IsCancellationRequested)
        {
          int read = await _server.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
          if (read == 0)
          {
            return;
          }
        }
      }
      catch (Exception ex) when (ex is OperationCanceledException or IOException)
      {
        // expected teardown
      }
    }, _cts.Token);
  }

  /// <summary>Drains the pipe at 4KB per 50ms until the connection dies. Deliberately slow
  ///     enough that a 1MB write cannot finish inside a short bound.</summary>
  private async Task DrainSlowlyAsync(NamedPipeServerStream server)
  {
    byte[] buffer = new byte[4096];
    try
    {
      while (!_cts.IsCancellationRequested)
      {
        int read = await server.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
        if (read == 0)
        {
          return; // peer closed
        }

        await Task.Delay(50, _cts.Token).ConfigureAwait(false);
      }
    }
    catch (Exception ex) when (ex is OperationCanceledException or IOException)
    {
      // expected teardown
    }
  }

  public async ValueTask DisposeAsync()
  {
    // Wait for the accept to settle so teardown never races a half-open connection.
    try
    {
      await _accept.ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is OperationCanceledException or IOException)
    {
      // expected teardown
    }

    // Disconnect first: a server pipe whose peer is blocked writing never observes EOF
    // otherwise, and the test's own teardown would hang.
    await _cts.CancelAsync().ConfigureAwait(false);
    _server.Disconnect();
    await _server.DisposeAsync().ConfigureAwait(false);
    _cts.Dispose();
  }
}
