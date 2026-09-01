
namespace eThangAgent.Transport.ACL.Tests;

/// <summary>Real named-pipe round trips over a unique pipe name per test: envelopes
///     survive both directions; a disposed connection surfaces as a declared failure.</summary>
public class NamedPipeChildTransportTests
{
  [Fact]
  public async Task AppToHost_And_HostToApp_EnvelopesSurvive()
  {
    string pipeName = "ethang-test-" + Guid.NewGuid().ToString("N");
    Task<NamedPipeChildTransport> serverTask = NamedPipeChildTransport.AcceptAppAsync(pipeName, TestContext.Current.CancellationToken);
    NamedPipeChildTransport app = await NamedPipeChildTransport.ConnectToHostAsync(pipeName,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    NamedPipeChildTransport host = await serverTask.ConfigureAwait(true);

    try
    {
      await app.SendAsync(new TransportEnvelope("deliver", "{}", 7), TestContext.Current.CancellationToken).ConfigureAwait(true);
      TransportEnvelope atHost = await host.ReceiveAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.Equal("deliver", atHost.Kind);
      Assert.Equal(7, atHost.Sequence);

      await host.SendAsync(new TransportEnvelope("event", "{}", 8), TestContext.Current.CancellationToken).ConfigureAwait(true);
      TransportEnvelope atApp = await app.ReceiveAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.Equal("event", atApp.Kind);
    }
    finally
    {
      await app.DisposeAsync().ConfigureAwait(true);
      await host.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task DisposedPeer_Receive_ThrowsDeclaredClosed()
  {
    string pipeName = "ethang-test-" + Guid.NewGuid().ToString("N");
    Task<NamedPipeChildTransport> serverTask = NamedPipeChildTransport.AcceptAppAsync(pipeName, TestContext.Current.CancellationToken);
    NamedPipeChildTransport app = await NamedPipeChildTransport.ConnectToHostAsync(pipeName,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    NamedPipeChildTransport host = await serverTask.ConfigureAwait(true);

    await host.DisposeAsync().ConfigureAwait(true);
    try
    {
      _ = await Assert.ThrowsAnyAsync<IOException>(
          () => app.ReceiveAsync(TestContext.Current.CancellationToken)).ConfigureAwait(true);
    }
    finally
    {
      await app.DisposeAsync().ConfigureAwait(true);
    }
  }
}
