using System.Text.Json;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>The host's DECLARED LIVE SET is ownership (review finding 1 on commit 63a034b):
///     a child in that set was never STARTED by this runtime instance, so an ownership gate
///     fed only by Start would refuse it. The declared set must therefore seed ownership - a
///     child the host declares live IS owned by this runtime's connection - and it must be
///     RECONCILED rather than accumulated, because the host re-declares after every start
///     and settle.
///     <para>Two facts make this contract load-bearing even though re-attach is retired
///     (issue #274): the declared set is the EXACT input to startup orphan repair, and it is
///     what the host says about the children it is actually running. The wire shape is
///     unchanged; only the 'survived a restart' motivation is gone.</para></summary>
public class RemoteRuntimeReAttachOwnershipTests
{
  private static async Task<(NamedPipeChildTransport App, NamedPipeChildTransport Host)> ConnectAsync()
  {
    string pipeName = "ethang-reattach-own-" + Guid.NewGuid().ToString("N");
    Task<NamedPipeChildTransport> serverTask = NamedPipeChildTransport.AcceptAppAsync(
        pipeName, TestContext.Current.CancellationToken);
    NamedPipeChildTransport app = await NamedPipeChildTransport.ConnectToHostAsync(pipeName,
        TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
    NamedPipeChildTransport host = await serverTask.WaitAsync(TimeSpan.FromSeconds(10),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    return (app, host);
  }

  private static PendingMessage Steering(string text) =>
      PendingMessage.New(text, MessageUrgency.Attention, "parent:root");

  /// <summary>Waits until the runtime has observed the host's declared set. The declare
  ///     envelope is asynchronous on the wire, so the test polls the OBSERVABLE fact
  ///     (DeclaredLiveChildren) rather than assuming pump timing.</summary>
  private static async Task WaitForDeclaredAsync(RemoteAgentRuntime runtime, Guid child)
  {
    for (int i = 0; i < 100; i++)
    {
      if (runtime.DeclaredLiveChildren.Contains(child))
      {
        return;
      }

      await Task.Delay(TimeSpan.FromMilliseconds(20),
          TestContext.Current.CancellationToken).ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task Deliver_ToAChildTheHostDeclaredLive_Succeeds()
  {
    AgentId child = new(Guid.NewGuid());

    // The app starts the child, so this runtime owns it by Start.
    (NamedPipeChildTransport firstApp, NamedPipeChildTransport firstHost) = await ConnectAsync().ConfigureAwait(true);
    RemoteAgentRuntime firstRuntime = new(firstApp);
    using CancellationTokenSource firstLoop = new();
    _ = firstRuntime.RunReceiveLoopAsync(firstLoop.Token);
    Result<AgentId> started = await firstRuntime.Start(
        AgentRecord.Spawned(child, null, 1, "m/sub", "reattach", "task", DateTimeOffset.UtcNow),
        TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(started.IsSuccess, started.Error?.Message);
    Assert.True(firstRuntime.Deliver(child, Steering("before reconnect")).IsSuccess,
        "the first runtime must steer a child it started");

    // The first connection goes away (the runtime is replaced, as it is whenever a session
    // reconnects). Both halves are disposed so the pipes are released.
    await firstApp.DisposeAsync().ConfigureAwait(true);
    await firstHost.DisposeAsync().ConfigureAwait(true);

    // A FRESH runtime that never started this child. The host declares its live set on
    // connect - the child it is still running.
    (NamedPipeChildTransport secondApp, NamedPipeChildTransport secondHost) = await ConnectAsync().ConfigureAwait(true);
    try
    {
      RemoteAgentRuntime secondRuntime = new(secondApp);
      using CancellationTokenSource secondLoop = new();
      _ = secondRuntime.RunReceiveLoopAsync(secondLoop.Token);
      await secondHost.SendAsync(new TransportEnvelope("declare",
          JsonSerializer.Serialize(new DeclareCommand([child.Value])), 1),
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      await WaitForDeclaredAsync(secondRuntime, child.Value).ConfigureAwait(true);

      // The host declares it live, so this runtime owns it: steering must reach it.
      // Before the fix this failed NotRunning and the message was lost.
      Result<bool> delivered = secondRuntime.Deliver(child, Steering("after reconnect"));
      Assert.True(delivered.IsSuccess,
          "a child the host declared live must be steerable: " + delivered.Error?.Message);
    }
    finally
    {
      await secondApp.DisposeAsync().ConfigureAwait(true);
      await secondHost.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task Deliver_ToAnIdTheHostNeverDeclared_StillFailsNotRunning()
  {
    (NamedPipeChildTransport app, NamedPipeChildTransport host) = await ConnectAsync().ConfigureAwait(true);
    try
    {
      RemoteAgentRuntime runtime = new(app);
      using CancellationTokenSource loop = new();
      _ = runtime.RunReceiveLoopAsync(loop.Token);

      // The host declares an EMPTY live set: nothing is owned.
      await host.SendAsync(new TransportEnvelope("declare",
          JsonSerializer.Serialize(new DeclareCommand([])), 1),
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      await Task.Delay(TimeSpan.FromMilliseconds(200),
          TestContext.Current.CancellationToken).ConfigureAwait(true);

      Result<bool> delivered = runtime.Deliver(new AgentId(Guid.NewGuid()), Steering("nobody home"));
      Assert.False(delivered.IsSuccess);
      Assert.Equal(MailboxErrors.NotRunning, delivered.Error.Code);
    }
    finally
    {
      await app.DisposeAsync().ConfigureAwait(true);
      await host.DisposeAsync().ConfigureAwait(true);
    }
  }
  /// <summary>Ownership must be RECONCILED, not merely accumulated (review round 2, minor
  ///     4): the host re-declares its live set after every start and settle, so an id it
  ///     stops declaring is a child that is no longer live there. Keeping it owned would
  ///     report a delivery as successful while the host silently drops it
  ///     (ChildHostServer.HandleDeliver ignores ids it does not run) - a lying receipt,
  ///     which is worse than the honest NotRunning this answered before the seed existed.</summary>
  [Fact]
  public async Task Deliver_ToAChildTheHostStoppedDeclaring_FailsNotRunningAgain()
  {
    AgentId child = new(Guid.NewGuid());
    (NamedPipeChildTransport app, NamedPipeChildTransport host) = await ConnectAsync().ConfigureAwait(true);
    try
    {
      RemoteAgentRuntime runtime = new(app);
      using CancellationTokenSource loop = new();
      _ = runtime.RunReceiveLoopAsync(loop.Token);

      // The host declares the child live: it is owned, and steering reaches it.
      await host.SendAsync(new TransportEnvelope("declare",
          JsonSerializer.Serialize(new DeclareCommand([child.Value])), 1),
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      await WaitForDeclaredAsync(runtime, child.Value).ConfigureAwait(true);
      Assert.True(runtime.Deliver(child, Steering("while live")).IsSuccess,
          "a declared child must be steerable");

      // The child settles host-side, so the host re-declares an EMPTY live set.
      await host.SendAsync(new TransportEnvelope("declare",
          JsonSerializer.Serialize(new DeclareCommand([])), 2),
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      await WaitForDeclaredCountAsync(runtime, 0).ConfigureAwait(true);

      // It is no longer live there: the runtime must stop claiming it. A success here
      // would be a receipt for a message the host drops on the floor.
      Result<bool> delivered = runtime.Deliver(child, Steering("after it settled"));
      Assert.False(delivered.IsSuccess,
          "a child the host no longer declares must not accept deliveries");
      Assert.Equal(MailboxErrors.NotRunning, delivered.Error.Code);
      Assert.DoesNotContain(child.Value, runtime.OwnedChildren);
    }
    finally
    {
      await app.DisposeAsync().ConfigureAwait(true);
      await host.DisposeAsync().ConfigureAwait(true);
    }
  }

  /// <summary>Waits until the runtime observes a declared set of the given size.</summary>
  private static async Task WaitForDeclaredCountAsync(RemoteAgentRuntime runtime, int count)
  {
    for (int i = 0; i < 100; i++)
    {
      if (runtime.DeclaredLiveChildren.Count == count)
      {
        return;
      }

      await Task.Delay(TimeSpan.FromMilliseconds(20),
          TestContext.Current.CancellationToken).ConfigureAwait(true);
    }
  }
}
