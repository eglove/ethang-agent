using eThangAgent.Lsp.ACL;
using eThangAgent.TestSupport;

namespace eThangAgent.Lsp.ACL.Tests;

[Trait("Category", "Process")]
public class LspDaemonTests
{
  [Fact]
  public void PipeName_ForWorkspaceId_Matches351Rule()
  {
    string name = LspPipeNames.ForWorkspaceId("ws-abc");
    Assert.StartsWith("ethang-lsp-", name);
    Assert.Equal(16, name.Length - "ethang-lsp-".Length);
    Assert.Equal(name, LspPipeNames.ForWorkspaceId("ws-abc"));
    Assert.NotEqual(name, LspPipeNames.ForWorkspaceId("ws-def"));
  }

  [Fact]
  public void CodeDetection_CSharpWorkspace_ReturnsFound()
  {
    string root = TestTempRoot.CreateDirectory("lspcode").FullName;
    try
    {
      File.WriteAllText(Path.Combine(root, "a.cs"), "class A {}");
      Assert.Equal(CodeDetectionResult.Found, CodeDetector.Detect(root));
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public void CodeDetection_EmptyWorkspace_ReturnsNotFound()
  {
    string root = TestTempRoot.CreateDirectory("lspempty").FullName;
    try
    {
      File.WriteAllText(Path.Combine(root, "notes.md"), "hello");
      Assert.Equal(CodeDetectionResult.NotFound, CodeDetector.Detect(root));
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public void CodeDetection_Inconclusive_ReturnsInconclusive()
  {
    string root = TestTempRoot.CreateDirectory("lspinc").FullName;
    try
    {
      for (int i = 0; i < 2500; i++) File.WriteAllText(Path.Combine(root, $"f{i}.md"), "x");
      Assert.Equal(CodeDetectionResult.Inconclusive, CodeDetector.Detect(root));
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task Pipe_HelloHandshake_Succeeds()
  {
    string ws = "ws-hello";
    string pipe = LspPipeNames.ForWorkspaceId(ws);
    using LspDaemonServer server = new(pipe, ws, () => Task.CompletedTask);
    using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
    Task serve = Task.Run(async () => await server.ServeOnceAsync(cts.Token));
    await Task.Delay(100, cts.Token);
    using LspPipeClient client = await LspPipeClient.ConnectAsync(pipe, ws, "app-1", cts.Token);
    Assert.Equal(ws, client.WorkspaceId);
    Assert.Equal(1, client.ProtocolVersion);
    client.Dispose();
    await serve;
  }

  [Fact]
  public async Task Pipe_WorkspaceMismatch_ReturnsUnavailable()
  {
    string pipe = LspPipeNames.ForWorkspaceId("ws-real");
    using LspDaemonServer server = new(pipe, "ws-real", () => Task.CompletedTask);
    using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
    Task serve = Task.Run(async () => await server.ServeOnceAsync(cts.Token));
    await Task.Delay(100, cts.Token);
    await Assert.ThrowsAsync<LspWorkspaceMismatchException>(async () =>
    {
      using LspPipeClient client = await LspPipeClient.ConnectAsync(pipe, "ws-other", "app-2", cts.Token);
    });
    await serve;
  }
}
