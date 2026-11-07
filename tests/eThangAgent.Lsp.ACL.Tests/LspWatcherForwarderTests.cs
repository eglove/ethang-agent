using eThangAgent.Lsp.ACL;
using eThangAgent.ToolDomain;

namespace eThangAgent.Lsp.ACL.Tests;

/// <summary>The forwarder carries watcher announcements to whichever daemon is current.
/// It must resolve the client per announcement: a client captured when the subscription
/// started keeps pointing at a daemon that has since died, so document sync would stop
/// silently after a reconnect.</summary>
[Trait("Category", "Process")]
public class LspWatcherForwarderTests
{
  private sealed class ScriptedWatcher : IFileWatcher
  {
    private Func<FileChangeAnnouncement, Task>? _handler;

    public long Generation => 0;

    public IDisposable Subscribe(Func<FileChangeAnnouncement, Task> handler)
    {
      _handler = handler;
      return new NoSubscription();
    }

    public Task PublishAsync(FileChangeAnnouncement announcement) => _handler!(announcement);

    private sealed class NoSubscription : IDisposable
    {
      public void Dispose()
      {
      }
    }
  }

  [Fact]
  public async Task Publish_ResolvesTheClientPerAnnouncement_NotAtStart()
  {
    ScriptedWatcher watcher = new();
    int resolved = 0;
    LspWatcherForwarder forwarder = new(watcher, "ws-fwd", _ =>
    {
      resolved++;
      return Task.FromResult<LspPipeClient?>(null);
    });

    forwarder.Start();
    Assert.Equal(0, resolved);

    await watcher.PublishAsync(new FileChangeAnnouncement(["a.cs"], [], 1, DateTimeOffset.UtcNow)).ConfigureAwait(true);
    Assert.Equal(1, resolved);

    await watcher.PublishAsync(new FileChangeAnnouncement(["a.cs"], [], 2, DateTimeOffset.UtcNow)).ConfigureAwait(true);
    Assert.Equal(2, resolved);

    forwarder.Dispose();
  }

  [Fact]
  public void Start_IsIdempotent()
  {
    ScriptedWatcher watcher = new();
    LspWatcherForwarder forwarder = new(watcher, "ws-fwd", _ => Task.FromResult<LspPipeClient?>(null));
    forwarder.Start();
    forwarder.Start();
    Assert.True(forwarder.IsForwarding);
    forwarder.Dispose();
    Assert.False(forwarder.IsForwarding);
  }
}
