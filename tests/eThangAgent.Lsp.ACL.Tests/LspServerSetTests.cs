using System.Text.Json.Nodes;
using eThangAgent.Lsp.ACL;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain.Lsp;

namespace eThangAgent.Lsp.ACL.Tests;

/// <summary>The daemon hosts one server per installed and detected language: what it starts,
///     what it starts when one language is missing or broken, how a request is routed by the
///     path's extension, and that shutting it down leaves no server process behind. The
///     starter is a fake, so the set's own behaviour is what is under test.</summary>
public class LspServerSetTests
{
  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  private static readonly string[] CSharpAndRust = ["csharp", "rust"];
  private static readonly string[] CSharpOnly = ["csharp"];

  private static string NewTree() => TestTempRoot.CreateDirectory("lspserverset").FullName;

  private static LspServerLaunch Launch(string languageId) =>
      new(languageId, "C:\\servers\\" + languageId + ".exe", "1.0.0");

  /// <summary>A starter that hands back a transport over memory streams, records every
  ///     launch it was asked for, and can be told to fail one language.</summary>
  private sealed class FakeStarter(params string[] failing) : ILspServerStarter
  {
    private readonly HashSet<string> _failing = new(failing, StringComparer.Ordinal);

    public List<string> Started { get; } = [];

    public int Disposed { get; private set; }

    public Task<LspServerConnection> StartAsync(LspServerLaunch launch, string workspaceRoot, CancellationToken ct)
    {
      if (_failing.Contains(launch.LanguageId))
      {
        throw new InvalidOperationException("the " + launch.LanguageId + " server crashed on start");
      }

      Started.Add(launch.LanguageId);
      MemoryStream input = new();
      MemoryStream output = new();
      LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
      LspClient client = new(transport, new LspServerConfig(
          launch.LanguageId, launch.ServerPath, [], LspServerCatalog.ForLanguage(launch.LanguageId)!.Extensions));
      return Task.FromResult(new LspServerConnection(transport, client, process: null, release: () => Disposed++));
    }
  }

  private sealed class FakeDiagnostics(string languageId) : ILspDiagnosticsQuery
  {
    private readonly string _languageId = languageId;

    public List<string> Asked { get; } = [];

    public Task<LspDiagnosticsResult> PullAsync(string uri, CancellationToken ct = default)
    {
      Asked.Add(uri);
      return Task.FromResult(new LspDiagnosticsResult(
          LspDiagnosticsState.Ready,
          [new LspDiagnostic(uri, 0, 0, 0, 1, _languageId, "reported by " + _languageId, 1)],
          _languageId));
    }

    public void BeginRestart()
    {
    }

    public Task CompleteRestartAsync() => Task.CompletedTask;
  }

  private sealed class FakeSymbols(string languageId) : ILspSymbolQuery
  {
    private readonly string _languageId = languageId;

    public List<string> Asked { get; } = [];

    public Task<LspSymbolResult> ExecuteAsync(
        string action, string uri, string symbolName, int occurrence, int depth, string newName,
        CancellationToken ct = default)
    {
      Asked.Add(action + " " + uri);
      return Task.FromResult(new LspSymbolResult(
          LspSymbolState.Ready, [new LspSymbolSite(uri, 1, 2, _languageId)], occurrence, 1, LanguageId: _languageId));
    }
  }

  /// <summary>Swaps each started server's services for recorders, so a request's route is
  ///     observable without a language server on the other end.</summary>
  private static LspServerSet WithRecordingServices(LspServerSet set)
  {
    List<LspRunningServer> replaced = [];
    foreach (LspRunningServer server in set.Servers)
    {
      replaced.Add(new LspRunningServer(
          server.Row, server.Launch,
          server.Connection,
          server.Documents,
          new FakeDiagnostics(server.LanguageId),
          new FakeSymbols(server.LanguageId)));
    }

    return LspServerSet.FromServers(replaced);
  }

  [Fact]
  public async Task Start_OneLaunchPerLanguage_StartsOneServerEach()
  {
    FakeStarter starter = new();
    using LspServerSet set = await LspServerSet.StartAsync(
        [Launch("csharp"), Launch("rust")], "ws-1", NewTree(), starter, Ct);

    Assert.Equal(CSharpAndRust, starter.Started);
    Assert.Equal(2, set.Count);
    Assert.Empty(set.Failed);
    Assert.Equal("csharp", set.Servers[0].LanguageId);
    Assert.Equal("rust", set.Servers[1].LanguageId);
  }

  /// <summary>The plan's example: a C# and a Rust workspace with both installed runs two
  ///     servers, and each request reaches the one claiming its extension.</summary>
  [Fact]
  public async Task Requests_AreRoutedToTheServerClaimingTheExtension()
  {
    FakeStarter starter = new();
    LspServerSet started = await LspServerSet.StartAsync(
        [Launch("csharp"), Launch("rust")], "ws-2", NewTree(), starter, Ct);
    using LspServerSet set = WithRecordingServices(started);

    LspDiagnosticsResult csharp = await set.GetDiagnosticsAsync("C:\\ws\\a.cs", Ct);
    LspDiagnosticsResult rust = await set.GetDiagnosticsAsync("C:\\ws\\a.rs", Ct);

    Assert.Equal("csharp", Assert.Single(csharp.Diagnostics).Code);
    Assert.Equal("rust", Assert.Single(rust.Diagnostics).Code);

    LspSymbolResult csharpSymbol = await set.QuerySymbolAsync(
        "definition", LspUriMapper.ToUri("C:\\ws\\a.cs"), "Foo", 0, 1, string.Empty, Ct);
    LspSymbolResult rustSymbol = await set.QuerySymbolAsync(
        "definition", LspUriMapper.ToUri("C:\\ws\\a.rs"), "Foo", 0, 1, string.Empty, Ct);

    Assert.Equal("csharp", Assert.Single(csharpSymbol.Sites).SymbolName);
    Assert.Equal("rust", Assert.Single(rustSymbol.Sites).SymbolName);

    Assert.Equal("csharp", csharp.LanguageId);
    Assert.Equal("rust", rust.LanguageId);
    Assert.Equal("csharp", csharpSymbol.LanguageId);
    Assert.Equal("rust", rustSymbol.LanguageId);
  }

  /// <summary>The plan's example: a Rust workspace on a machine without rust-analyzer runs
  ///     only the C# server, and a request for a Rust file is empty rather than an error.
  ///     </summary>
  [Fact]
  public async Task UnclaimedPath_ReturnsAnEmptyResultAndNoError()
  {
    FakeStarter starter = new();
    LspServerSet started = await LspServerSet.StartAsync(
        [Launch("csharp")], "ws-3", NewTree(), starter, Ct);
    using LspServerSet set = WithRecordingServices(started);

    LspDiagnosticsResult diagnostics = await set.GetDiagnosticsAsync("C:\\ws\\a.rs", Ct);
    LspSymbolResult symbol = await set.QuerySymbolAsync(
        "definition", LspUriMapper.ToUri("C:\\ws\\a.rs"), "Foo", 0, 1, string.Empty, Ct);

    Assert.Empty(diagnostics.Diagnostics);
    Assert.Empty(symbol.Sites);
    Assert.Equal(LspSymbolState.Ready, symbol.State);
  }

  /// <summary>The plan's example: rust-analyzer crashes on start and the C# server still
  ///     answers requests.</summary>
  [Fact]
  public async Task ALaunchThatFails_DoesNotPreventTheOthersFromServing()
  {
    FakeStarter starter = new("rust");
    LspServerSet started = await LspServerSet.StartAsync(
        [Launch("csharp"), Launch("rust")], "ws-4", NewTree(), starter, Ct);
    using LspServerSet set = WithRecordingServices(started);

    Assert.Equal(1, started.Count);
    Assert.Equal("rust", Assert.Single(started.Failed).LanguageId);
    LspDiagnosticsResult result = await set.GetDiagnosticsAsync("C:\\ws\\a.cs", Ct);
    Assert.Equal("csharp", Assert.Single(result.Diagnostics).Code);
  }

  /// <summary>A language nobody installed is a launch that was never produced, so the set
  ///     simply holds fewer servers and the request for that language stays empty.
  ///     </summary>
  [Fact]
  public async Task NoLaunchForALanguage_StartsNothingAndIsNotAnError()
  {
    FakeStarter starter = new();
    LspServerSet started = await LspServerSet.StartAsync([Launch("csharp")], "ws-5", NewTree(), starter, Ct);
    using LspServerSet set = WithRecordingServices(started);

    Assert.Equal(CSharpOnly, starter.Started);
    Assert.Null(set.ForLanguage("rust"));
    Assert.Empty((await set.GetDiagnosticsAsync("C:\\ws\\a.rs", Ct)).Diagnostics);
  }

  [Fact]
  public async Task EmptySet_AnswersEveryRequestWithAnEmptyResult()
  {
    using LspServerSet set = await LspServerSet.StartAsync([], "ws-6", NewTree(), new FakeStarter(), Ct);

    Assert.Equal(0, set.Count);
    Assert.Empty((await set.GetDiagnosticsAsync("C:\\ws\\a.cs", Ct)).Diagnostics);
    Assert.Empty((await set.QuerySymbolAsync(
        "definition", LspUriMapper.ToUri("C:\\ws\\a.cs"), "Foo", 0, 1, string.Empty, Ct)).Sites);
  }

  /// <summary>A watcher announcement reaches every running server, and each applies only the
  ///     paths its own row claims.</summary>
  [Fact]
  public async Task Announcement_ReachesEveryServer_AndEachAppliesOnlyItsOwnPaths()
  {
    string root = NewTree();
    try
    {
      await File.WriteAllTextAsync(Path.Combine(root, "a.cs"), "class A { }", Ct);
      await File.WriteAllTextAsync(Path.Combine(root, "a.rs"), "fn main() {}", Ct);
      FakeStarter starter = new();
      using LspServerSet set = await LspServerSet.StartAsync(
          [Launch("csharp"), Launch("rust")], "ws-7", root, starter, Ct);

      await set.ApplyAnnouncementAsync(
          new WatcherAnnouncement("ws-7", ["a.cs", "a.rs"], [], 1, DateTimeOffset.UtcNow), Ct);

      LspRunningServer csharp = set.ForLanguage("csharp")!;
      LspRunningServer rust = set.ForLanguage("rust")!;
      Assert.Equal(["a.cs"], csharp.Documents.OpenDocuments);
      Assert.Equal(["a.rs"], rust.Documents.OpenDocuments);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  /// <summary>The plan's example: shutting the daemon down leaves no child server process
  ///     behind, which for the set means every started connection was released.</summary>
  [Fact]
  public async Task Dispose_ReleasesEveryStartedServer()
  {
    FakeStarter starter = new();
    LspServerSet set = await LspServerSet.StartAsync(
        [Launch("csharp"), Launch("rust"), Launch("python")], "ws-8", NewTree(), starter, Ct);

    set.Dispose();

    Assert.Equal(3, starter.Disposed);
    Assert.Equal(0, set.Count);
  }

  /// <summary>The daemon's own announcement format is what the status monitor parses, so a
  ///     routed daemon's stdout still reads as server lifecycle rather than as noise.
  ///     </summary>
  [Fact]
  public void AnnouncementLines_StillParse()
  {
    Assert.True(LspServerInstallLine.TryParse("server-resolving ws-1", out LspServerInstallState resolving, out _));
    Assert.Equal(LspServerInstallState.Downloading, resolving);
    Assert.True(LspServerInstallLine.TryParse("server-ready 1.0.0", out LspServerInstallState ready, out string version));
    Assert.Equal(LspServerInstallState.Ready, ready);
    Assert.Equal("1.0.0", version);
    Assert.True(LspServerInstallLine.TryParse("server-unavailable", out LspServerInstallState unavailable, out _));
    Assert.Equal(LspServerInstallState.Unavailable, unavailable);
  }

  [Fact]
  public void EmptySet_HoldsNothing()
  {
    Assert.Equal(0, LspServerSet.Empty.Count);
    Assert.Empty(LspServerSet.Empty.Servers);
  }

  /// <summary>The plan's example: a shell file whose server advertises no rename answers with
  ///     the action and the language. The capabilities record is the fake, and the service
  ///     behind the set is the real one, so the message under test is the one a caller sees.
  ///     </summary>
  [Fact]
  public async Task Rename_OnAServerWithoutTheCapability_NamesTheActionAndTheLanguage()
  {
    FakeStarter starter = new();
    LspServerSet started = await LspServerSet.StartAsync([Launch("bash")], "ws-9", NewTree(), starter, Ct);
    using LspServerSet set = WithRealSymbolService(started, LspCapabilities.None with { DocumentSymbolProvider = true });

    LspSymbolResult rename = await set.QuerySymbolAsync(
        "rename", LspUriMapper.ToUri("C:\\ws\\run.sh"), "greet", 0, 1, "hello", Ct);

    Assert.Equal(LspSymbolState.Unsupported, rename.State);
    Assert.Equal("rename is not supported by the bash server", rename.Message);
    Assert.Equal("bash", rename.LanguageId);
  }

  /// <summary>A supported action on the same server still returns its payload, so the
  ///     unsupported answer is a gap in one capability and not a broken server.</summary>
  [Fact]
  public async Task ASupportedAction_OnTheSameServer_StillReturnsItsPayload()
  {
    FakeStarter starter = new();
    LspServerSet started = await LspServerSet.StartAsync([Launch("bash")], "ws-10", NewTree(), starter, Ct);
    using LspServerSet set = WithRealSymbolService(started, LspCapabilities.None with { DocumentSymbolProvider = true });
    set.ForLanguage("bash")!.Transport.EnqueueIncomingForTest(
        new JsonObject
        {
          ["jsonrpc"] = "2.0",
          ["id"] = 1,
          ["result"] = new JsonArray(new JsonObject
          {
            ["name"] = "greet",
            ["range"] = Span(0, 6, 0, 11),
            ["selectionRange"] = Span(0, 6, 0, 11),
          }),
        }.ToJsonString());

    LspSymbolResult symbols = await set.QuerySymbolAsync(
        "document-symbols", LspUriMapper.ToUri("C:\\ws\\run.sh"), string.Empty, 0, 1, string.Empty, Ct);

    Assert.Equal(LspSymbolState.Ready, symbols.State);
    Assert.Equal("bash", symbols.LanguageId);
    Assert.Equal("greet", Assert.Single(symbols.Sites).SymbolName);
  }

  private static JsonObject Span(int startLine, int startCharacter, int endLine, int endCharacter)
    => new()
    {
      ["start"] = new JsonObject { ["line"] = startLine, ["character"] = startCharacter },
      ["end"] = new JsonObject { ["line"] = endLine, ["character"] = endCharacter },
    };

  /// <summary>Swaps each started server's symbol query for the real service over that server's
  ///     own transport, so a capability gate is exercised without a language server behind it.
  ///     </summary>
  private static LspServerSet WithRealSymbolService(LspServerSet set, LspCapabilities capabilities)
  {
    List<LspRunningServer> replaced = [];
    foreach (LspRunningServer server in set.Servers)
    {
      replaced.Add(new LspRunningServer(
          server.Row, server.Launch,
          server.Connection,
          server.Documents,
          server.Diagnostics,
          new LspServerSymbolQuery(
              new LspSymbolService(server.Transport, capabilities, workspace: null, languageId: server.LanguageId))));
    }

    return LspServerSet.FromServers(replaced);
  }
}
