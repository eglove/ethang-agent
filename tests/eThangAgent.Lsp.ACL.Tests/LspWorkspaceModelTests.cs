using eThangAgent.Lsp.ACL;
using eThangAgent.TestSupport;

// JSON002 fires only in the format/IDE host; the pragma pair is the repo's named pattern.
#pragma warning disable JSON002 // Probable JSON string detected

namespace eThangAgent.Lsp.ACL.Tests;

// Issue #365 defect 4: Roslyn answers an empty diagnostic set for a workspace it has not
// loaded, so the solution load is the precondition for any diagnostic at all. These pin
// the model's two facts - the load is requested once, and a workspace counts as loaded
// only when the server says so.
public class LspWorkspaceModelTests
{
  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  private static LspDocumentSync Forwarder(string root, string workspaceId) =>
      new(workspaceId, root, "csharp", [".cs"], new RecordingSync());

  private sealed class RecordingSync : ILspDocumentSync
  {
    public List<string> Calls { get; } = [];

    public Task DidOpenAsync(string uri, string languageId, int version, string text, CancellationToken ct = default)
    {
      Calls.Add("open:" + uri + ":" + version);
      return Task.CompletedTask;
    }

    public Task DidChangeAsync(string uri, int version, string text, CancellationToken ct = default)
    {
      Calls.Add("change:" + uri + ":" + version);
      return Task.CompletedTask;
    }

    public Task DidCloseAsync(string uri, CancellationToken ct = default)
    {
      Calls.Add("close:" + uri);
      return Task.CompletedTask;
    }
  }

  /// <summary>A project file is not a solution: solution/open rejects it, and a workspace
  ///     with only a .csproj is covered by the server's file-based project system.</summary>
  [Fact]
  public void WorkspaceSolution_AcceptsOnlyASolutionFile()
  {
    string root = TestTempRoot.CreateDirectory("lsp-solution").FullName;
    try
    {
      File.WriteAllText(Path.Combine(root, "a.csproj"), "<Project />");
      Assert.Null(LspWorkspaceSolution.Find(root));
      File.WriteAllText(Path.Combine(root, "a.slnx"), "<Solution />");
      Assert.Equal(Path.Combine(root, "a.slnx"), LspWorkspaceSolution.Find(root));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public void WorkspaceSolution_NoManifest_ReturnsNull()
  {
    string root = TestTempRoot.CreateDirectory("lsp-nosolution").FullName;
    try
    {
      File.WriteAllText(Path.Combine(root, "notes.md"), "hello");
      Assert.Null(LspWorkspaceSolution.Find(root));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task Model_RequestsTheLoadOnce_AndIsNotLoadedUntilTheServerSaysSo()
  {
    string root = TestTempRoot.CreateDirectory("lsp-model").FullName;
    try
    {
      File.WriteAllText(Path.Combine(root, "a.slnx"), "<Solution />");
      File.WriteAllText(Path.Combine(root, "a.cs"), "class A { }");
      using MemoryStream input = new();
      using MemoryStream output = new();
      LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
      RecordingSync sink = new();
      LspRoslynWorkspaceModel model = new(
          transport, new LspDocumentSync("ws-model", root, "csharp", [".cs"], sink), root);
      string uri = LspUriMapper.ToUri(Path.Combine(root, "a.cs"));

      Assert.False(model.IsLoaded);
      await model.PrepareAsync(uri, Ct);
      Assert.False(model.IsLoaded);

      string written = System.Text.Encoding.UTF8.GetString(output.ToArray());
      Assert.Contains("solution/open", written, StringComparison.Ordinal);
      // A document is not opened before the load completes: the server has no project to
      // attach it to, and the sync it receives then is discarded.
      Assert.DoesNotContain("textDocument/didOpen", written, StringComparison.Ordinal);

      _ = model.HandleNotification("{\"jsonrpc\":\"2.0\",\"method\":\"workspace/projectInitializationComplete\"}");
      Assert.True(model.IsLoaded);

      output.SetLength(0);
      await model.PrepareAsync(uri, Ct);
      Assert.DoesNotContain("solution/open", System.Text.Encoding.UTF8.GetString(output.ToArray()), StringComparison.Ordinal);
      Assert.StartsWith("open:", Assert.Single(sink.Calls), StringComparison.Ordinal);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task Model_NoSolutionFile_IsLoadedWithoutWaiting()
  {
    string root = TestTempRoot.CreateDirectory("lsp-filebased").FullName;
    try
    {
      using MemoryStream input = new();
      using MemoryStream output = new();
      LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
      LspRoslynWorkspaceModel model = new(transport, Forwarder(root, "ws-filebased"), root);
      Assert.True(model.IsLoaded);
      await model.PrepareAsync(LspUriMapper.ToUri(Path.Combine(root, "a.cs")), Ct);
      Assert.DoesNotContain("solution/open", System.Text.Encoding.UTF8.GetString(output.ToArray()), StringComparison.Ordinal);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task Diagnostics_UnloadedWorkspace_ReportsStartingRatherThanClean()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    LspDiagnosticsService service = new(
        transport,
        new LspDiagnosticStore(),
        new LspCapabilities(false, false, false, false, false, false, false, true, false),
        new UnloadedModel());
    LspDiagnosticsResult result = await service.PullAsync("file:///C:/ws/a.cs", Ct);
    Assert.Equal(LspDiagnosticsState.Starting, result.State);
    Assert.Empty(result.Diagnostics);
    Assert.DoesNotContain("textDocument/diagnostic", System.Text.Encoding.UTF8.GetString(output.ToArray()), StringComparison.Ordinal);
  }

  private sealed class UnloadedModel : ILspWorkspaceModel
  {
    public bool IsLoaded => false;

    public Task PrepareAsync(string uri, CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> EnsureLoadedAsync(TimeSpan timeout, CancellationToken ct = default) => Task.FromResult(false);
  }
}

#pragma warning restore JSON002
