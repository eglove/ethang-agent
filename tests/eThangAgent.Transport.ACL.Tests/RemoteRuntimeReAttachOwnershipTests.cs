using System.Text.Json;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>Ownership after a RE-ATTACH (review finding 1 on commit 63a034b): a child the
///     host kept running across an app restart is in the declared live set but was never
///     STARTED by the new runtime, so an ownership gate fed only by Start refuses it.
///     Steering to a surviving child is the exact scenario the host-survives-restart plan
///     exists to serve, so the declared set must seed ownership: a child the host declares
///     live IS owned by this runtime's connection. Genuinely unknown ids still fail
///     NotRunning - that half is pinned here too, so the fix cannot regress it.</summary>
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
      new(text, MessageUrgency.Attention, DateTimeOffset.UtcNow, "parent:root");

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
  public async Task Deliver_ToAChildTheHostDeclaredLive_AfterReAttach_Succeeds()
  {
    AgentId child = new(Guid.NewGuid());

    // FIRST connection: the app starts the child, so the FIRST runtime owns it by Start.
    (NamedPipeChildTransport firstApp, NamedPipeChildTransport firstHost) = await ConnectAsync().ConfigureAwait(true);
    RemoteAgentRuntime firstRuntime = new(firstApp);
    using CancellationTokenSource firstLoop = new();
    _ = firstRuntime.RunReceiveLoopAsync(firstLoop.Token);
    Result<AgentId> started = await firstRuntime.Start(
        AgentRecord.Spawned(child, null, 1, "m/sub", "reattach", "task", DateTimeOffset.UtcNow),
        TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(started.IsSuccess, started.Error?.Message);
    Assert.True(firstRuntime.Deliver(child, Steering("before restart")).IsSuccess,
        "the first runtime must steer a child it started");

    // The app dies; the host survives, still running that child.
    await firstApp.DisposeAsync().ConfigureAwait(true);
    await firstHost.DisposeAsync().ConfigureAwait(true);

    // SECOND connection: a FRESH runtime that never started this child. The host
    // declares its live set on connect - the child it kept running.
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

      // The host declares it live, so the re-attached runtime owns it: steering must
      // reach it. Before the fix this failed NotRunning and the message was lost.
      Result<bool> delivered = secondRuntime.Deliver(child, Steering("after restart"));
      Assert.True(delivered.IsSuccess,
          "a child the host declared live must be steerable after re-attach: " + delivered.Error?.Message);
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
}
