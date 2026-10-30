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
///     unchanged; only the 'survived a restart' motivation is gone.</para>
///     <para>WHY THESE ASSERT ON <c>OwnedChildren</c> AND NOT ON A DELIVERY (issue #316):
///     the ownership gate used to be observed through <c>IAgentRuntime.Deliver</c>, and
///     agent-to-agent messaging is retired, so that seam is gone. The ownership FACT is not:
///     <c>OwnedChildren</c> is still what startup orphan repair reads to decide which child
///     rows are live, so getting it wrong would still strand rows or mark live ones dead.
///     These tests now assert the fact itself rather than a receipt that happened to expose
///     it - a narrower claim about the same rule, not a weaker one.</para></summary>
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
  public async Task DeclaredChild_IsOwned_EvenByARuntimeThatNeverStartedIt()
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
    Assert.Contains(child.Value, firstRuntime.OwnedChildren);

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
      Assert.DoesNotContain(child.Value, secondRuntime.OwnedChildren);
      await secondHost.SendAsync(new TransportEnvelope("declare",
          JsonSerializer.Serialize(new DeclareCommand([child.Value])), 1),
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      await WaitForDeclaredAsync(secondRuntime, child.Value).ConfigureAwait(true);

      // The host declares it live, so this runtime OWNS it. Before the fix the declared set
      // never seeded ownership, so orphan repair would have called a live child dead.
      Assert.Contains(child.Value, secondRuntime.OwnedChildren);
    }
    finally
    {
      await secondApp.DisposeAsync().ConfigureAwait(true);
      await secondHost.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task AnIdTheHostNeverDeclared_IsNotOwned()
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

      Assert.Empty(runtime.OwnedChildren);
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
  ///     report a dead row as live to orphan repair - the stranded-row defect in the other
  ///     direction.</summary>
  [Fact]
  public async Task AChildTheHostStoppedDeclaring_StopsBeingOwned()
  {
    AgentId child = new(Guid.NewGuid());
    (NamedPipeChildTransport app, NamedPipeChildTransport host) = await ConnectAsync().ConfigureAwait(true);
    try
    {
      RemoteAgentRuntime runtime = new(app);
      using CancellationTokenSource loop = new();
      _ = runtime.RunReceiveLoopAsync(loop.Token);

      // The host declares the child live: it is owned.
      await host.SendAsync(new TransportEnvelope("declare",
          JsonSerializer.Serialize(new DeclareCommand([child.Value])), 1),
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      await WaitForDeclaredAsync(runtime, child.Value).ConfigureAwait(true);
      Assert.Contains(child.Value, runtime.OwnedChildren);

      // The child settles host-side, so the host re-declares an EMPTY live set.
      await host.SendAsync(new TransportEnvelope("declare",
          JsonSerializer.Serialize(new DeclareCommand([])), 2),
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      await WaitForDeclaredCountAsync(runtime, 0).ConfigureAwait(true);

      // It is no longer live there: the runtime must stop claiming it.
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
