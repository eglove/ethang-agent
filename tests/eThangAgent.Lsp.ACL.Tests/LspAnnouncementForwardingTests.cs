using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using eThangAgent.Lsp.ACL;
using eThangAgent.TestSupport;

namespace eThangAgent.Lsp.ACL.Tests;

// Issue #362: watcher-to-daemon announcement forwarding. The pipe path is
// proven alone: these tests never touch IFileWatcher pub/sub.
[Trait("Category", "Process")]
public class LspAnnouncementForwardingTests
{
  private static string NewTree() => TestTempRoot.CreateDirectory("lspforward").FullName;

  private static void Write(string root, string relative, string content)
  {
    string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content);
  }

  private sealed class FakeSync : ILspDocumentSync
  {
    public List<string> Calls { get; } = [];
    public Task DidOpenAsync(string uri, string languageId, int version, string text, CancellationToken ct = default)
    {
      Calls.Add("open:" + uri + ":" + version + ":" + text);
      return Task.CompletedTask;
    }
    public Task DidChangeAsync(string uri, int version, string text, CancellationToken ct = default)
    {
      Calls.Add("change:" + uri + ":" + version + ":" + text);
      return Task.CompletedTask;
    }
    public Task DidCloseAsync(string uri, CancellationToken ct = default)
    {
      Calls.Add("close:" + uri);
      return Task.CompletedTask;
    }
  }

  [Fact]
  public void Codec_RoundTrips_WatcherAnnounceWireFormat()
  {
    WatcherAnnouncement announcement = new("ws-1", ["a.cs", "sub/b.cs"], ["gone.cs"], 7, DateTimeOffset.Parse("2026-11-04T12:00:00Z", CultureInfo.InvariantCulture));
    string line = WatcherAnnouncementCodec.Encode(announcement);
    Assert.Contains("\"method\":\"watcher.announce\"", line);
    WatcherAnnouncement? decoded = WatcherAnnouncementCodec.TryDecode(line);
    Assert.NotNull(decoded);
    Assert.Equal("ws-1", decoded.WorkspaceId);
    Assert.Equal(["a.cs", "sub/b.cs"], decoded.Paths);
    Assert.Equal(["gone.cs"], decoded.DeletedPaths);
    Assert.Equal(7, decoded.Generation);
    Assert.Equal(DateTimeOffset.Parse("2026-11-04T12:00:00Z", CultureInfo.InvariantCulture), decoded.UtcTimestamp);
  }

  [Fact]
  public void Codec_Malformed_ReturnsNull()
  {
    Assert.Null(WatcherAnnouncementCodec.TryDecode("not json"));
    string otherMethod = "{" + "\"method\":\"other\",\"params\":{}}";
    Assert.Null(WatcherAnnouncementCodec.TryDecode(otherMethod));
    string noParams = "{" + "\"method\":\"watcher.announce\"}";
    Assert.Null(WatcherAnnouncementCodec.TryDecode(noParams));
  }

  [Fact]
  public async Task Forward_NewFile_SendsDidOpenWithDiskTextAndVersion()
  {
    string root = NewTree();
    try
    {
      Write(root, "a.cs", "class A {}");
      FakeSync sync = new();
      LspDocumentSync forwarder = new("ws-1", root, "csharp", [".cs"], sync);
      WatcherAnnouncement announcement = new("ws-1", ["a.cs"], [], 1, DateTimeOffset.UtcNow);
      await forwarder.ApplyAsync(announcement, TestContext.Current.CancellationToken);
      string call = Assert.Single(sync.Calls);
      Assert.StartsWith("open:", call);
      Assert.Contains("a.cs", call);
      Assert.Contains(":1:", call);
      Assert.Contains("class A {}", call);
      Assert.Equal(1, forwarder.LastAppliedGeneration);
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task Forward_ChangedFile_SendsDidChangeWithFreshDiskText()
  {
    string root = NewTree();
    try
    {
      Write(root, "a.cs", "v1");
      FakeSync sync = new();
      LspDocumentSync forwarder = new("ws-1", root, "csharp", [".cs"], sync);
      await forwarder.ApplyAsync(new WatcherAnnouncement("ws-1", ["a.cs"], [], 1, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
      Write(root, "a.cs", "v2");
      await forwarder.ApplyAsync(new WatcherAnnouncement("ws-1", ["a.cs"], [], 2, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
      Assert.Equal(2, sync.Calls.Count);
      Assert.StartsWith("change:", sync.Calls[1]);
      Assert.Contains("v2", sync.Calls[1]);
      Assert.Contains(":2:", sync.Calls[1]);
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task Forward_DeletedPath_SendsDidClose()
  {
    string root = NewTree();
    try
    {
      Write(root, "a.cs", "x");
      FakeSync sync = new();
      LspDocumentSync forwarder = new("ws-1", root, "csharp", [".cs"], sync);
      await forwarder.ApplyAsync(new WatcherAnnouncement("ws-1", ["a.cs"], [], 1, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
      await forwarder.ApplyAsync(new WatcherAnnouncement("ws-1", [], ["a.cs"], 2, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
      Assert.Equal(2, sync.Calls.Count);
      Assert.StartsWith("close:", sync.Calls[1]);
      Assert.Contains("a.cs", sync.Calls[1]);
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task Forward_ReadFailure_SendsDidClose()
  {
    string root = NewTree();
    try
    {
      FakeSync sync = new();
      LspDocumentSync forwarder = new("ws-1", root, "csharp", [".cs"], sync);
      await forwarder.ApplyAsync(new WatcherAnnouncement("ws-1", ["missing.cs"], [], 1, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
      string call = Assert.Single(sync.Calls);
      Assert.StartsWith("close:", call);
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task Forward_StaleGeneration_IsIgnored()
  {
    string root = NewTree();
    try
    {
      Write(root, "a.cs", "v2");
      FakeSync sync = new();
      LspDocumentSync forwarder = new("ws-1", root, "csharp", [".cs"], sync);
      await forwarder.ApplyAsync(new WatcherAnnouncement("ws-1", ["a.cs"], [], 2, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
      await forwarder.ApplyAsync(new WatcherAnnouncement("ws-1", ["a.cs"], [], 1, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
      await forwarder.ApplyAsync(new WatcherAnnouncement("ws-1", ["a.cs"], [], 2, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
      Assert.Single(sync.Calls);
      Assert.Equal(2, forwarder.LastAppliedGeneration);
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task Forward_UnclaimedExtension_IsIgnored()
  {
    string root = NewTree();
    try
    {
      Write(root, "notes.md", "hello");
      FakeSync sync = new();
      LspDocumentSync forwarder = new("ws-1", root, "csharp", [".cs"], sync);
      await forwarder.ApplyAsync(new WatcherAnnouncement("ws-1", ["notes.md"], [], 1, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
      Assert.Empty(sync.Calls);
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task Forward_WorkspaceMismatch_IsIgnored()
  {
    string root = NewTree();
    try
    {
      Write(root, "a.cs", "x");
      FakeSync sync = new();
      LspDocumentSync forwarder = new("ws-1", root, "csharp", [".cs"], sync);
      await forwarder.ApplyAsync(new WatcherAnnouncement("ws-other", ["a.cs"], [], 1, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
      Assert.Empty(sync.Calls);
      Assert.Equal(0, forwarder.LastAppliedGeneration);
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task Restart_ReopensAllOpenDocuments_WithFreshDiskText()
  {
    string root = NewTree();
    try
    {
      Write(root, "a.cs", "v1");
      Write(root, "b.cs", "b1");
      FakeSync first = new();
      LspDocumentSync before = new("ws-1", root, "csharp", [".cs"], first);
      await before.ApplyAsync(new WatcherAnnouncement("ws-1", ["a.cs", "b.cs"], [], 1, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
      Write(root, "a.cs", "v2-after-restart");
      FakeSync second = new();
      LspDocumentSync after = new("ws-1", root, "csharp", [".cs"], second);
      await after.ReopenAllAsync(before.OpenDocuments, TestContext.Current.CancellationToken);
      Assert.Equal(2, second.Calls.Count);
      Assert.All(second.Calls, c => Assert.StartsWith("open:", c));
      Assert.Contains(second.Calls, c => c.Contains("a.cs") && c.Contains("v2-after-restart"));
      Assert.Contains(second.Calls, c => c.Contains("b.cs") && c.Contains("b1"));
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task PipeOnly_Announcement_ReachesServerAsDidChange_WithoutPubSub()
  {
    string root = NewTree();
    try
    {
      Write(root, "a.cs", "v1");
      string ws = "ws-pipeonly";
      string pipe = LspPipeNames.ForWorkspaceId(ws);
      FakeSync sync = new();
      LspDocumentSync forwarder = new(ws, root, "csharp", [".cs"], sync);
      await forwarder.ApplyAsync(new WatcherAnnouncement(ws, ["a.cs"], [], 1, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
      Write(root, "a.cs", "v2-pipe");
      using LspDaemonServer server = new(pipe, ws, () => Task.CompletedTask);
      server.AttachForwarder(forwarder);
      using CancellationTokenSource cts = new(TestCeiling.Value);
      Task serve = Task.Run(async () => await server.ServeUntilClosedAsync(cts.Token));
      await Task.Delay(100, cts.Token);
      using LspPipeClient client = await LspPipeClient.ConnectAsync(pipe, ws, "app-1", cts.Token);
      WatcherAnnouncement announcement = new(ws, ["a.cs"], [], 2, DateTimeOffset.UtcNow);
      await client.SendWatcherAnnouncementAsync(announcement, cts.Token);
      await Task.Delay(500, cts.Token);
      Assert.Equal(2, sync.Calls.Count);
      Assert.StartsWith("change:", sync.Calls[1]);
      Assert.Contains("v2-pipe", sync.Calls[1]);
      Assert.Equal(2, forwarder.LastAppliedGeneration);
      client.Dispose();
      cts.Cancel();
      try { await serve; } catch (OperationCanceledException) { /* expected on cancel */ }
    }
    finally { Directory.Delete(root, true); }
  }
}
