using System.IO.Pipes;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>A peer that ACCEPTS the connection and then never reads a byte — the
///     "host accepted but never reads" probe. A named-pipe write blocks once the peer's
///     buffer fills, so a large enough send against this peer never completes. Exists to
///     prove the app-side send path is bounded rather than relying on the peer's manners.</summary>
internal sealed class NonReadingPeer : IAsyncDisposable
{
  private readonly NamedPipeServerStream _server;
  private readonly Task _accept;

  private NonReadingPeer(NamedPipeServerStream server, string pipeName, Task accept)
  {
    _server = server;
    PipeName = pipeName;
    _accept = accept;
  }

  public string PipeName { get; }

  /// <summary>Creates the pipe and returns AT ONCE; the connection is awaited in the
  ///     background. Awaiting it here would deadlock: the peer would wait for a client that
  ///     the caller only connects after this returns.</summary>
  public static Task<NonReadingPeer> StartAsync()
  {
    string pipeName = "ethang-noread-" + Guid.NewGuid().ToString("N");
    NamedPipeServerStream server = new(pipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    NonReadingPeer peer = new(server, pipeName,
        Task.Run(() => server.WaitForConnectionAsync(TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken));
    // Deliberately NO read loop: once connected the peer consumes nothing.
    return Task.FromResult(peer);
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
    _server.Disconnect();
    await _server.DisposeAsync().ConfigureAwait(false);
  }
}
