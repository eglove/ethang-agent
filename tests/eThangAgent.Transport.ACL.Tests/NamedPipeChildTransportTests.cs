
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

  /// <summary>A client that connects and goes away BEFORE the host's accept is issued must
  ///     not kill the host: the listening instance exists from the constructor, so a connect can
  ///     complete against an instance whose ConnectNamedPipe has not run yet, and the wait then
  ///     fails with ERROR_NO_DATA ("The pipe is being closed"). Measured 2026-10-26: 20/20 at
  ///     every non-zero delay. That is a client nobody served, never the connection the host
  ///     serves being lost - so the accept RE-ARMS and the next real client is served.
  ///     <para>This is the defect that made ReAttachE2ETests fail only in a full-solution run:
  ///     the test connected and immediately dropped, the host died of the disturbed accept, and
  ///     the failure looked like a startup race. A retrying CONNECT could never fix it, because
  ///     the connect was already succeeding.</para></summary>
  [Fact]
  public async Task DisturbedAccept_ReArms_AndServesTheNextClient()
  {
    string pipeName = "ethang-test-" + Guid.NewGuid().ToString("N");

    // The seam runs inside the window between the listening instance existing and
    // ConnectNamedPipe being issued. A ghost dials and leaves there, which is exactly what
    // makes the accept fail with ERROR_NO_DATA ("The pipe is being closed"). The window
    // cannot be hit on demand from outside - measured 2026-10-26, a client that dials before
    // the accept is armed is simply served (0/40 disturbed) - so it is injected.
    // The two arms are SEQUENCED so nothing races: arm 1 is disturbed by the ghost, and arm 2
    // signals that the fresh instance exists and the poisoned one is gone. Only then does the
    // real client dial, so it can never connect to the instance that is about to be discarded.
    int arms = 0;
    TaskCompletionSource reArmed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task<NamedPipeChildTransport> serverTask = NamedPipeChildTransport.AcceptAppAsync(pipeName,
        async () =>
        {
          if (Interlocked.Increment(ref arms) == 1)
          {
            NamedPipeChildTransport ghost = await NamedPipeChildTransport.ConnectToHostAsync(pipeName,
                TestContext.Current.CancellationToken).ConfigureAwait(false);
            await ghost.DisposeAsync().ConfigureAwait(false);
            return;
          }

          _ = reArmed.TrySetResult();
        }, TestContext.Current.CancellationToken);

    await reArmed.Task.WaitAsync(TimeSpan.FromSeconds(15),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    // The accept must survive the disturbance and serve a REAL client.
    NamedPipeChildTransport app = await NamedPipeChildTransport.ConnectToHostAsync(pipeName,
        TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(15),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
    NamedPipeChildTransport host = await serverTask.WaitAsync(TimeSpan.FromSeconds(15),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    try
    {
      await app.SendAsync(new TransportEnvelope("deliver", "{}", 1), TestContext.Current.CancellationToken).ConfigureAwait(true);
      TransportEnvelope atHost = await host.ReceiveAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.Equal("deliver", atHost.Kind);
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
