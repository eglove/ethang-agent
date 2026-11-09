using eThangAgent.Lsp.ACL;
using eThangAgent.TestSupport;

// JSON002 fires only in the format/IDE host; the pragma pair is the repo's named pattern.
#pragma warning disable JSON002 // Probable JSON string detected

namespace eThangAgent.Lsp.ACL.Tests;

// A workspace with no solution has no compilation, so the server answers from a file-based
// fallback that parses but resolves nothing. A report from that state is not a clean file:
// an unresolved type, a wrong argument and an unused using are all invisible to it. These
// pin the rule from the symptom - "clean" reaching the caller when the file does not
// compile - not from the mechanism that produces it.
public class LspNoCompilationTests
{
  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  /// <summary>A workspace whose server is loaded but holds no compilation.</summary>
  private sealed class NoCompilationModel : ILspWorkspaceModel
  {
    public bool IsLoaded => true;

    public bool HasCompilation => false;

    public Task PrepareAsync(string uri, CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> EnsureLoadedAsync(TimeSpan timeout, CancellationToken ct = default) => Task.FromResult(true);
  }

  private static LspDiagnosticsService Service(ILspWorkspaceModel model) =>
      new(new LspJsonRpcTransport(new MemoryStream(), new MemoryStream(), TimeSpan.FromSeconds(2)),
          new LspDiagnosticStore(),
          new LspCapabilities(false, false, false, false, false, false, false, true, false),
          model);

  /// <summary>THE SYMPTOM: a file that does not compile, in a workspace with no
  ///     compilation, reported as Ready with no diagnostics - which reads as clean.</summary>
  [Fact]
  public async Task EmptyReport_WithoutACompilation_IsNotClean()
  {
    LspDiagnosticsResult result = await Service(new NoCompilationModel()).PullAsync("file:///C:/ws/Broken.cs", Ct);

    Assert.NotEqual(LspDiagnosticsState.Ready, result.State);
    Assert.Empty(result.Diagnostics);
  }

  /// <summary>A syntax error needs no compilation to detect, so it is real evidence and is
  ///     still delivered - labeled, because the rest of the file was not analysed.</summary>
  [Fact]
  public async Task SyntaxReport_WithoutACompilation_IsStillDeliveredAndLabeled()
  {
    LspDiagnosticStore store = new();
    store.OnPullDiagnostics("file:///C:/ws/Broken.cs",
        "{\"items\":[{\"range\":{\"start\":{\"line\":4,\"character\":0},\"end\":{\"line\":4,\"character\":1}},\"code\":\"CS1513\",\"message\":\"} expected\"}]}");
    LspDiagnosticsService service = new(
        new LspJsonRpcTransport(new MemoryStream(), new MemoryStream(), TimeSpan.FromSeconds(2)),
        store,
        new LspCapabilities(false, false, false, false, false, false, false, true, false),
        new NoCompilationModel());

    LspDiagnosticsResult result = await service.PullAsync("file:///C:/ws/Broken.cs", Ct);

    Assert.Equal(LspDiagnosticsState.Degraded, result.State);
    Assert.Contains(result.Diagnostics, d => d.Code == "CS1513");
  }

  /// <summary>A workspace WITH a compilation is answered normally: the rule narrows what
  ///     counts as evidence, it does not disable the advisory.</summary>
  [Fact]
  public async Task Report_WithACompilation_IsReady()
  {
    using MemoryStream input = new();
    using MemoryStream output = new();
    LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
    transport.EnqueueIncomingForTest("{\"jsonrpc\":\"2.0\",\"id\":3,\"result\":{\"kind\":\"full\",\"items\":[]}}");
    LspDiagnosticsService service = new(
        transport,
        new LspDiagnosticStore(),
        new LspCapabilities(false, false, false, false, false, false, false, true, false),
        new LoadedModel());

    LspDiagnosticsResult result = await service.PullAsync("file:///C:/ws/Ok.cs", Ct);

    Assert.Equal(LspDiagnosticsState.Ready, result.State);
    Assert.Empty(result.Diagnostics);
  }

  private sealed class LoadedModel : ILspWorkspaceModel
  {
    public bool IsLoaded => true;

    public bool HasCompilation => true;

    public Task PrepareAsync(string uri, CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> EnsureLoadedAsync(TimeSpan timeout, CancellationToken ct = default) => Task.FromResult(true);
  }
}

#pragma warning restore JSON002
