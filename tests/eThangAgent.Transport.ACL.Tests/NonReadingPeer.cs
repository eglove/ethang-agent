using System.IO.Pipes;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>A peer that ACCEPTS the connection and then either never reads a byte or reads
///     far too slowly — the "host accepted but is not keeping up" probes. A named-pipe write
///     blocks once the peer's buffer fills, so a large enough send against this peer does not
///     complete in time. Exists to prove the app-side send path is bounded rather than
///     relying on the peer's manners.
/// <para>Two modes, because the DIAGNOSIS differs and must be accurate: a peer that reads
///     NOTHING is genuinely "not reading", while a peer draining 4KB every 50ms IS reading
///     and must not be reported that way (the re-reviewer's ProbeI2).</para></summary>
internal sealed class NonReadingPeer : IAsyncDisposable
{
  private readonly NamedPipeServerStream _server;
  private readonly Task _accept;
  private readonly CancellationTokenSource _cts = new();

  private NonReadingPeer(NamedPipeServerStream server, string pipeName, Task accept, bool slowReader)
  {
    _server = server;
    PipeName = pipeName;
    _accept = accept;
    if (slowReader)
    {
      _ = Task.Run(async () =>
      {
        await accept.ConfigureAwait(false);
        await DrainSlowlyAsync(server).ConfigureAwait(false);
      }, _cts.Token);
    }
  }

  public string PipeName { get; }

  /// <summary>Creates the pipe and returns AT ONCE; the connection is awaited in the
  ///     background. Awaiting it here would deadlock: the peer would wait for a client that
  ///     the caller only connects after this returns.</summary>
  public static Task<NonReadingPeer> StartAsync()
      => StartCoreAsync(slowReader: false);

  /// <summary>A peer that DOES read, just far slower than the writer produces (4KB every
  ///     50ms). The distinction matters: this peer is reading, so the failure must not claim
  ///     it is not.</summary>
  public static Task<NonReadingPeer> StartSlowReaderAsync()
      => StartCoreAsync(slowReader: true);

  private static Task<NonReadingPeer> StartCoreAsync(bool slowReader)
  {
    string pipeName = (slowReader ? "ethang-slowread-" : "ethang-noread-")
        + Guid.NewGuid().ToString("N");
    NamedPipeServerStream server = new(pipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    NonReadingPeer peer = new(server, pipeName,
        Task.Run(() => server.WaitForConnectionAsync(TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken),
        slowReader);
    return Task.FromResult(peer);
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
