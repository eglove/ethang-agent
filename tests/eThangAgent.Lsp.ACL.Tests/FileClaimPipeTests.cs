using eThangAgent.Lsp.ACL;
using eThangAgent.ToolDomain.FileClaims;

namespace eThangAgent.Lsp.ACL.Tests;

// Issue #363: the daemon-side shared claim store. Two pipe clients share one
// daemon table; refusal payloads round-trip holder session id + expiry;
// expiry-on-read holds over the pipe with no sweeper.
[Trait("Category", "Process")]
public class FileClaimPipeTests
{
  [Fact]
  public async Task TwoClients_SameFile_SecondRefusedNamingHolderAndExpiry_FirstClaimIntact()
  {
    string ws = "ws-claims";
    string pipe = LspPipeNames.ForWorkspaceId(ws);
    SharedFileClaimStore store = new();
    using LspDaemonServer server = new(pipe, ws, () => Task.CompletedTask, store);
    using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
    Task serve = Task.Run(async () => await server.ServeUntilClosedAsync(cts.Token));
    await Task.Delay(100, cts.Token);
    using LspPipeClient first = await LspPipeClient.ConnectAsync(pipe, ws, "app-1", cts.Token);
    FileClaimResult a = await first.AcquireClaimAsync("C:\\ws\\a.cs", "s-1", cts.Token);
    Assert.True(a.Granted);
    Assert.Equal("s-1", a.HolderSessionId);
    Assert.NotNull(a.Expiry);
    first.Dispose();
    using LspPipeClient second = await LspPipeClient.ConnectAsync(pipe, ws, "app-2", cts.Token);
    FileClaimResult b = await second.AcquireClaimAsync("C:\\ws\\a.cs", "s-2", cts.Token);
    Assert.False(b.Granted);
    Assert.Equal("s-1", b.HolderSessionId);
    Assert.Equal(a.Expiry, b.Expiry);
    FileClaim? peek = store.Peek(ws, "C:\\ws\\a.cs", DateTimeOffset.UtcNow);
    Assert.NotNull(peek);
    Assert.Equal("s-1", peek.SessionId);
    Assert.Equal(a.Expiry, peek.Expiry);
    second.Dispose();
    cts.Cancel();
    try { await serve; } catch (OperationCanceledException) { /* expected on cancel */ }
  }

  [Fact]
  public async Task Renew_RefreshesExpiry_OverPipe()
  {
    string ws = "ws-claims-renew";
    string pipe = LspPipeNames.ForWorkspaceId(ws);
    SharedFileClaimStore store = new();
    using LspDaemonServer server = new(pipe, ws, () => Task.CompletedTask, store);
    using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
    Task serve = Task.Run(async () => await server.ServeUntilClosedAsync(cts.Token));
    await Task.Delay(100, cts.Token);
    using LspPipeClient client = await LspPipeClient.ConnectAsync(pipe, ws, "app-1", cts.Token);
    FileClaimResult a = await client.AcquireClaimAsync("C:\\ws\\a.cs", "s-1", cts.Token);
    Assert.True(a.Granted);
    FileClaimResult renewed = await client.RenewClaimAsync("C:\\ws\\a.cs", "s-1", cts.Token);
    Assert.True(renewed.Granted);
    Assert.NotNull(renewed.Expiry);
    Assert.True(renewed.Expiry > a.Expiry);
    client.Dispose();
    cts.Cancel();
    try { await serve; } catch (OperationCanceledException) { /* expected on cancel */ }
  }

  [Fact]
  public async Task Release_Frees_OverPipe()
  {
    string ws = "ws-claims-release";
    string pipe = LspPipeNames.ForWorkspaceId(ws);
    SharedFileClaimStore store = new();
    using LspDaemonServer server = new(pipe, ws, () => Task.CompletedTask, store);
    using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
    Task serve = Task.Run(async () => await server.ServeUntilClosedAsync(cts.Token));
    await Task.Delay(100, cts.Token);
    using LspPipeClient client = await LspPipeClient.ConnectAsync(pipe, ws, "app-1", cts.Token);
    Assert.True((await client.AcquireClaimAsync("C:\\ws\\a.cs", "s-1", cts.Token)).Granted);
    Assert.True(await client.ReleaseClaimAsync("C:\\ws\\a.cs", "s-1", cts.Token));
    Assert.True((await client.AcquireClaimAsync("C:\\ws\\a.cs", "s-2", cts.Token)).Granted);
    client.Dispose();
    cts.Cancel();
    try { await serve; } catch (OperationCanceledException) { /* expected on cancel */ }
  }
}
