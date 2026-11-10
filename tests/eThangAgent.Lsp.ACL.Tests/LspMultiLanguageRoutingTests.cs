using eThangAgent.Lsp.ACL;
using eThangAgent.TestSupport;

namespace eThangAgent.Lsp.ACL.Tests;

/// <summary>One workspace, several languages, several real server processes: the daemon
///     launches a server per installed and detected language and routes each request to the
///     process whose row claims the file's extension.
///     <para>THE SERVERS ARE REAL PROCESSES, not fakes behind the starter: a fake starter
///     produces no second process, so 'two processes served two languages' would be an
///     assertion about the test's own fake. The in-repo fake server is installed into a temp
///     install root at the layout the resolver reads, so the daemon resolves, launches, and
///     initializes it exactly as it would a downloaded one.</para></summary>
[Trait("Category", "Process")]
public class LspMultiLanguageRoutingTests
{
  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  /// <summary>A C# fixture whose declaration precedes its call, so the first occurrence of
  ///     the queried name is the declaration the service resolves against.</summary>
  private static readonly string[] CSharpSource =
  [
      "namespace Probe;",
      "",
      "public static class Program",
      "{",
      "  public static void Greet()",
      "  {",
      "  }",
      "",
      "  public static void Main()",
      "  {",
      "    Greet();",
      "  }",
      "}",
  ];

  /// <summary>A Rust fixture in the same shape.</summary>
  private static readonly string[] RustSource =
  [
      "fn greet() {",
      "}",
      "",
      "fn main() {",
      "    greet();",
      "}",
  ];

  [Fact]
  public async Task TwoInstalledLanguages_AreServedByTwoDistinctProcesses()
  {
    string root = TestTempRoot.CreateDirectory("lsp-multi-root").FullName;
    string installRoot = TestTempRoot.CreateDirectory("lsp-multi-install").FullName;
    string workspaceId = "ws-multi-" + Guid.NewGuid().ToString("N");
    try
    {
      string csharpFile = WriteFixture(root, "Program.cs", CSharpSource);
      string rustFile = WriteFixture(root, "main.rs", RustSource);
      LspServerSet set = await StartAsync(installRoot, workspaceId, root, ["csharp", "rust"]).ConfigureAwait(true);
      using (set)
      {
        Assert.Equal(2, set.Count);
        int[] pids = [.. set.Servers.Select(server => server.Connection.Process!.Id)];
        Assert.Equal(2, pids.Distinct().Count());

        using LspDaemonServer daemon = new(LspPipeNames.ForWorkspaceId(workspaceId), workspaceId, () => Task.CompletedTask);
        daemon.AttachServers(set);
        using CancellationTokenSource cts = new(TestCeiling.Value);
        Task serve = Task.Run(async () => await daemon.ServeUntilClosedAsync(cts.Token).ConfigureAwait(false), cts.Token);
        using LspPipeClient client = await ConnectAsync(workspaceId, Ct).ConfigureAwait(true);

        LspSymbolResult csharp = await DefinitionAsync(client, csharpFile, "Greet").ConfigureAwait(true);
        LspSymbolResult rust = await DefinitionAsync(client, rustFile, "greet").ConfigureAwait(true);

        Assert.Equal(LspSymbolState.Ready, csharp.State);
        Assert.Equal("csharp", csharp.LanguageId);
        Assert.Equal(DeclarationLine(CSharpSource, "Greet"), Assert.Single(csharp.Sites).Line);

        Assert.Equal(LspSymbolState.Ready, rust.State);
        Assert.Equal("rust", rust.LanguageId);
        Assert.Equal(DeclarationLine(RustSource, "greet"), Assert.Single(rust.Sites).Line);

        await cts.CancelAsync().ConfigureAwait(true);
        await serve.WaitAsync(TestCeiling.Value, CancellationToken.None).ConfigureAwait(true);
      }
    }
    finally
    {
      TryDelete(root);
      TryDelete(installRoot);
    }
  }

  [Fact]
  public async Task RemovingOneLanguage_LeavesTheWorkspacesOtherLanguageServing()
  {
    string root = TestTempRoot.CreateDirectory("lsp-multi-remove").FullName;
    string installRoot = TestTempRoot.CreateDirectory("lsp-multi-remove-install").FullName;
    string workspaceId = "ws-multi-remove-" + Guid.NewGuid().ToString("N");
    try
    {
      string csharpFile = WriteFixture(root, "Program.cs", CSharpSource);
      string rustFile = WriteFixture(root, "main.rs", RustSource);
      LspServerSet set = await StartAsync(installRoot, workspaceId, root, ["csharp", "rust"]).ConfigureAwait(true);
      using (set)
      {
        LspRunningServer rust = set.ForLanguage("rust")!;
        rust.Dispose();
        LspServerSet remaining = LspServerSet.FromServers([set.ForLanguage("csharp")!]);
        using (remaining)
        {
          using LspDaemonServer daemon = new(LspPipeNames.ForWorkspaceId(workspaceId), workspaceId, () => Task.CompletedTask);
          daemon.AttachServers(remaining);
          using CancellationTokenSource cts = new(TestCeiling.Value);
          Task serve = Task.Run(async () => await daemon.ServeUntilClosedAsync(cts.Token).ConfigureAwait(false), cts.Token);
          using LspPipeClient client = await ConnectAsync(workspaceId, Ct).ConfigureAwait(true);

          LspSymbolResult stillServed = await DefinitionAsync(client, csharpFile, "Greet").ConfigureAwait(true);
          LspSymbolResult removed = await DefinitionAsync(client, rustFile, "greet").ConfigureAwait(true);

          Assert.Equal(LspSymbolState.Ready, stillServed.State);
          Assert.Equal("csharp", stillServed.LanguageId);
          Assert.NotEmpty(stillServed.Sites);

          Assert.Empty(removed.Sites);
          Assert.NotEqual("rust", removed.LanguageId);

          await cts.CancelAsync().ConfigureAwait(true);
          await serve.WaitAsync(TestCeiling.Value, CancellationToken.None).ConfigureAwait(true);
        }
      }
    }
    finally
    {
      TryDelete(root);
      TryDelete(installRoot);
    }
  }

  /// <summary>Every language the catalog serves is launched as its own process and answers a
  ///     definition for a file of its own extension, so the routing table is exercised for the
  ///     whole catalog rather than for the two languages a fixture happened to name.</summary>
  [Fact]
  public async Task EveryCatalogLanguage_IsServedByItsOwnProcess()
  {
    string root = TestTempRoot.CreateDirectory("lsp-multi-all").FullName;
    string installRoot = TestTempRoot.CreateDirectory("lsp-multi-all-install").FullName;
    string workspaceId = "ws-multi-all-" + Guid.NewGuid().ToString("N");
    try
    {
      string[] languages = [.. LspServerCatalog.All.Select(row => row.LanguageId)];
      Dictionary<string, string> files = [];
      foreach (LspLanguageRow row in LspServerCatalog.All)
      {
        files[row.LanguageId] = WriteFixture(root, "probe" + row.Extensions[0],
            ["greet();", "", "fn greet() {", "}"]);
      }

      LspServerSet set = await StartAsync(installRoot, workspaceId, root, languages).ConfigureAwait(true);
      using (set)
      {
        Assert.Equal(languages.Length, set.Count);
        Assert.Empty(set.Failed);
        Assert.Equal(languages.Length, set.Servers.Select(server => server.Connection.Process!.Id).Distinct().Count());

        using LspDaemonServer daemon = new(LspPipeNames.ForWorkspaceId(workspaceId), workspaceId, () => Task.CompletedTask);
        daemon.AttachServers(set);
        using CancellationTokenSource cts = new(TestCeiling.Value);
        Task serve = Task.Run(async () => await daemon.ServeUntilClosedAsync(cts.Token).ConfigureAwait(false), cts.Token);
        using LspPipeClient client = await ConnectAsync(workspaceId, Ct).ConfigureAwait(true);

        foreach (string language in languages)
        {
          LspSymbolResult result = await DefinitionAsync(client, files[language], "greet").ConfigureAwait(true);
          Assert.Equal(LspSymbolState.Ready, result.State);
          Assert.Equal(language, result.LanguageId);
        }

        await cts.CancelAsync().ConfigureAwait(true);
        await serve.WaitAsync(TestCeiling.Value, CancellationToken.None).ConfigureAwait(true);
      }
    }
    finally
    {
      TryDelete(root);
      TryDelete(installRoot);
    }
  }

  /// <summary>Starts one real server per named language from the fake installed at the
  ///     layout the resolver reads, and asserts the resolver finds each of them there.</summary>
  private static async Task<LspServerSet> StartAsync(string installRoot, string workspaceId,
      string workspaceRoot, IReadOnlyList<string> languages)
  {
    List<LspServerLaunch> launches = [];
    foreach (string language in languages)
    {
      LspLanguageRow row = LspServerCatalog.ForLanguage(language)!;
      _ = StubLspServer.Install(installRoot, InstallDirectory(row), row.EntryPointRelativePath, row.ServerFileName);
      string? resolved = LspInstallLayout.InstalledEntryPoint(row, installRoot);
      Assert.NotNull(resolved);
      launches.Add(new LspServerLaunch(row.LanguageId, resolved, row.Version));
    }

    return await LspServerSet.StartAsync(launches, workspaceId, workspaceRoot, Ct).ConfigureAwait(true);
  }

  /// <summary>The directory segments the production install layout keys one row by: package
  ///     and version for a downloaded row, language and version for a toolchain row.</summary>
  private static string InstallDirectory(LspLanguageRow row) =>
      row.InstallKind == LspInstallKind.Toolchain
          ? Path.Combine(row.LanguageId, row.Version)
          : Path.Combine(row.PackageId, row.Version);

  private static async Task<LspSymbolResult> DefinitionAsync(LspPipeClient client, string path, string symbol)
    => await client.SymbolAsync("definition", LspUriMapper.ToUri(path), symbol, 0, 1, string.Empty, Ct)
        .ConfigureAwait(true);

  /// <summary>Waits until the daemon accepts a connection, so a query never races its
  ///     listen backlog.</summary>
  private static async Task<LspPipeClient> ConnectAsync(string workspaceId, CancellationToken ct)
  {
    string pipeName = LspPipeNames.ForWorkspaceId(workspaceId);
    for (int attempt = 0; attempt < 200; attempt++)
    {
      try
      {
        return await LspPipeClient.ConnectAsync(pipeName, workspaceId, "app-1", ct).ConfigureAwait(true);
      }
      catch (Exception ex) when (ex is TimeoutException or IOException)
      {
        await Task.Delay(25, ct).ConfigureAwait(true);
      }
    }

    throw new InvalidOperationException("the language-server daemon never accepted a connection");
  }

  private static string WriteFixture(string root, string fileName, string[] lines)
  {
    string path = Path.Combine(root, fileName);
    File.WriteAllLines(path, lines);
    return path;
  }

  /// <summary>The 0-based line the fixture declares <paramref name="symbol"/> on: the first
  ///     line that names it, which is the declaration in a fixture that declares before it
  ///     calls.</summary>
  private static int DeclarationLine(string[] lines, string symbol)
  {
    for (int i = 0; i < lines.Length; i++)
    {
      if (lines[i].Contains(symbol, StringComparison.Ordinal))
      {
        return i;
      }
    }

    throw new InvalidOperationException("the fixture never names " + symbol);
  }

  private static void TryDelete(string directory)
  {
    try
    {
      Directory.Delete(directory, recursive: true);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
    {
      _ = ex.Message;
    }
  }
}
