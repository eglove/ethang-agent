using eThangAgent.ChildHost;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>Task 1 (scoped stop): a subtree-root interrupt over the wire cancels ONLY
///     the ids in the root's subtree among the host's active children — another
///     session's child (a sibling root's descendant) is never touched, and a NULL-id
///     interrupt remains the legacy blanket cancel. The rig registers active children
///     with parent edges the way RunChildAsync does (AttachActiveChild), no real child
///     runs; the serve loop stays alive throughout.</summary>
public class ChildHostInterruptScopeTests
{
  [Fact]
  public async Task SubtreeRootInterrupt_CancelsOnlyThatSubtreesActiveChildren()
  {
    (ChildHostServer server, NamedPipeChildTransport appSide, Func<Task> _) = await StartRigAsync().ConfigureAwait(true);
    try
    {
      Guid sessionRoot = Guid.NewGuid();
      Guid ownChild = Guid.NewGuid();
      Guid ownGrandchild = Guid.NewGuid();
      (Guid otherId, CancellationTokenSource otherCts) = NewActive();
      // Named decision (CA2000): the CTSs' ownership transfers to the server's active
      // set — the run's finally (or the test's observation) owns their lifetime.
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
      server.AttachActiveChild(sessionRoot, parentId: null, NewCts());
      server.AttachActiveChild(ownChild, sessionRoot, NewCts());
      server.AttachActiveChild(ownGrandchild, ownChild, NewCts());
      server.AttachActiveChild(otherId, parentId: null, otherCts);
#pragma warning restore CA2000 // Call IDisposable.Dispose on object created by

      await appSide.SendAsync(new TransportEnvelope("interrupt",
          System.Text.Json.JsonSerializer.Serialize(new InterruptCommand(sessionRoot)), NextSeq()),
          TestContext.Current.CancellationToken).ConfigureAwait(true);

      // Every member of the session's subtree is cancelled — root, child, grandchild.
      await WaitUntil(() => Cancelled(server, sessionRoot) && Cancelled(server, ownChild)
          && Cancelled(server, ownGrandchild), TimeSpan.FromSeconds(10)).ConfigureAwait(true);
      // The OTHER session's child (outside the subtree) keeps running.
      Assert.False(Cancelled(server, otherId));
    }
    finally
    {
      await appSide.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task NullIdInterrupt_RemainsTheLegacyBlanketCancel()
  {
    (ChildHostServer server, NamedPipeChildTransport appSide, Func<Task> _) = await StartRigAsync().ConfigureAwait(true);
    try
    {
      (Guid idA, CancellationTokenSource ctsA) = NewActive();
      (Guid idB, CancellationTokenSource ctsB) = NewActive();
      // Named decision (CA2000): ownership transfers to the server's active set.
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
      server.AttachActiveChild(idA, parentId: null, ctsA);
      server.AttachActiveChild(idB, parentId: null, ctsB);
#pragma warning restore CA2000 // Call IDisposable.Dispose on object created by

      await appSide.SendAsync(new TransportEnvelope("interrupt",
          System.Text.Json.JsonSerializer.Serialize(new InterruptCommand(null)), NextSeq()),
          TestContext.Current.CancellationToken).ConfigureAwait(true);

      await WaitUntil(() => Cancelled(server, idA) && Cancelled(server, idB),
          TimeSpan.FromSeconds(10)).ConfigureAwait(true);
    }
    finally
    {
      await appSide.DisposeAsync().ConfigureAwait(true);
    }
  }

  private static (Guid Id, CancellationTokenSource Cts) NewActive()
      => (Guid.NewGuid(), NewCts());

  private static CancellationTokenSource NewCts() => new();

  private static bool Cancelled(ChildHostServer server, Guid id)
      => server.ActiveToken(id) is { } token && token.IsCancellationRequested;

  private static async Task WaitUntil(Func<bool> condition, TimeSpan limit)
  {
    DateTimeOffset deadline = DateTimeOffset.UtcNow + limit;
    while (!condition())
    {
      if (DateTimeOffset.UtcNow > deadline)
      {
        Assert.Fail("condition not met within the limit");
      }

      await Task.Delay(50, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }
  }

  private static long _seq;

  private static long NextSeq() => Interlocked.Increment(ref _seq);

  private static async Task<(ChildHostServer Server, NamedPipeChildTransport App, Func<Task> Serve)> StartRigAsync()
  {
    string settings = Path.Combine(Path.GetTempPath(), "ethang-interruptrig-" + Guid.NewGuid().ToString("N") + ".json");
    await File.WriteAllTextAsync(settings, /*lang=json,strict*/ "{\"OpenRouter\":{\"ApiKey\":null},\"Zai\":{\"ApiKey\":null}}",
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    ChildHostServer server = new(settings, Path.Combine(Path.GetTempPath(), "ethang-interruptrig.db"));
    string pipeName = "ethang-interruptrig-" + Guid.NewGuid().ToString("N");
    Task<NamedPipeChildTransport> accept = NamedPipeChildTransport.AcceptAppAsync(pipeName, TestContext.Current.CancellationToken);
    NamedPipeChildTransport app = await NamedPipeChildTransport.ConnectToHostAsync(pipeName,
        TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    NamedPipeChildTransport hostSide = await accept.WaitAsync(TimeSpan.FromSeconds(10),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    server.AttachTransport(hostSide);
    Task serve = server.ServeAsync();
    return (server, app, () => serve);
  }
}
