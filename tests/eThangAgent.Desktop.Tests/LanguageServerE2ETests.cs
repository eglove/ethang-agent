using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Lsp.ACL;
using eThangAgent.Storage.ACL;
using eThangAgent.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using LspSymbolState = eThangAgent.Lsp.ACL.LspSymbolState;

namespace eThangAgent.Desktop.Tests;

/// <summary>The multi-language language-server path end to end, over the REAL composed core:
///     a workspace holding only a non-C# language prompts for it, installing from the prompt
///     makes the workspace's daemon serve it, and a workspace holding C# and Rust is answered
///     for a <c>.cs</c> file and for a <c>.rs</c> file by two different server processes.
///     <para>THE INSTALL IS FAKED AND THE SERVERS ARE NOT: the installer is composed over the
///     real catalog and the real preference store with an install strategy that places the
///     in-repo stub server where the real resolver looks, so no download and no third-party
///     release page is involved and the daemon launches a real child process. The install root
///     is the app's own cache directory, because that is the only root the daemon reads.
///     </para>
///     <para>THE SERVE LEG RUNS OVER THE REAL PIPE THROUGH AN IN-PROCESS DAEMON, the shape the
///     diagnostics E2E already uses: the child daemon executable re-runs its restart hook on
///     every accepted connection, which starts a second server set and leaves the connection
///     unanswered, so a claim about routing is made where the routing actually runs.</para>
///     </summary>
[Collection("Desktop E2E")]
[Trait("Category", "Process")]
public class LanguageServerE2ETests
{
  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  /// <summary>A Rust workspace: a manifest and a source file whose call follows its
  ///     declaration.</summary>
  private static readonly string[] RustSource =
  [
      "fn greet() {",
      "}",
      "",
      "fn main() {",
      "    greet();",
      "}",
  ];

  /// <summary>A C# workspace: a solution, a project, and a source file whose call follows its
  ///     declaration. The solution is what lets Roslyn load a compilation at all.</summary>
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

  [Fact]
  public async Task AWorkspaceHoldingOnlyRust_Prompts_AndSpawnsNothing()
  {
    string root = TestTempRoot.CreateDirectory("lsp-e2e-rust-prompt").FullName;
    try
    {
      _ = WriteRustWorkspace(root);

#pragma warning disable CA2007 // 'await using' cannot carry ConfigureAwait.
      await using E2E.HostHarness host = await StartAsync(root).ConfigureAwait(true);
#pragma warning restore CA2007

      LanguagePromptViewModel prompt = await WaitForPromptAsync(host).ConfigureAwait(true);
      Assert.True(prompt.IsVisible);
      Assert.Equal("Rust support available", prompt.Message);
      Assert.Equal(["rust"], [.. prompt.Offers.Select(offer => offer.LanguageId)]);

      // The prompt is a statement about what this machine cannot serve, so nothing was
      // installed and no server process exists to serve it.
      Assert.Empty(host.Services.GetRequiredService<ILanguageServerInstaller>().InstalledLanguageIds());
      Assert.Empty(StubServerProcesses());
    }
    finally
    {
      TryDelete(root);
    }
  }

  [Fact]
  public async Task InstallAllOnThePrompt_InstallsRust_AndThePromptGoesAway()
  {
    string root = TestTempRoot.CreateDirectory("lsp-e2e-rust-install").FullName;
    try
    {
      _ = WriteRustWorkspace(root);

#pragma warning disable CA2007 // 'await using' cannot carry ConfigureAwait.
      await using E2E.HostHarness host = await StartAsync(root).ConfigureAwait(true);
#pragma warning restore CA2007

      LanguagePromptViewModel prompt = await WaitForPromptAsync(host).ConfigureAwait(true);
      Assert.True(prompt.IsVisible);

      // The prompt's own command, not the installer directly: this is the act a user performs.
      await prompt.InstallAllCommand.ExecuteAsync(null).ConfigureAwait(true);

      Assert.False(prompt.IsVisible);
      Assert.Equal(string.Empty, prompt.Message);

      ILanguageServerInstaller installer = host.Services.GetRequiredService<ILanguageServerInstaller>();
      await installer.RefreshAsync(Ct).ConfigureAwait(true);
      Assert.Contains("rust", installer.InstalledLanguageIds(), StringComparer.Ordinal);

      // The language the prompt installed is served, from a real server process, over the
      // workspace's own pipe.
      LspSymbolResult definition = await DefinitionAsync(root, ["rust"], RustFile(root), "greet").ConfigureAwait(true);
      Assert.Equal(LspSymbolState.Ready, definition.State);
      Assert.Equal("rust", definition.LanguageId);
      Assert.Equal([DeclarationLine(RustSource, "greet")], [.. definition.Sites.Select(site => site.Line)]);
    }
    finally
    {
      TryDelete(root);
    }
  }

  [Fact]
  public async Task AWorkspaceHoldingCSharpAndRust_IsServedByTwoDistinctServerProcesses()
  {
    string root = TestTempRoot.CreateDirectory("lsp-e2e-two").FullName;
    try
    {
      _ = WriteCSharpWorkspace(root);
      _ = WriteRustWorkspace(root);

#pragma warning disable CA2007 // 'await using' cannot carry ConfigureAwait.
      await using E2E.HostHarness host = await StartAsync(root).ConfigureAwait(true);
#pragma warning restore CA2007

      // C# is never offered: this app installs it without being asked.
      LanguagePromptViewModel prompt = await WaitForPromptAsync(host).ConfigureAwait(true);
      Assert.Equal(["rust"], [.. prompt.Offers.Select(offer => offer.LanguageId)]);
      Assert.Equal("Rust support available", prompt.Message);
      await prompt.InstallAllCommand.ExecuteAsync(null).ConfigureAwait(true);
      Assert.False(prompt.IsVisible);

      (LspSymbolResult rust, LspSymbolResult csharp, int processes) =
          await DefinitionsAsync(root).ConfigureAwait(true);

      Assert.Equal(LspSymbolState.Ready, rust.State);
      Assert.Equal("rust", rust.LanguageId);
      Assert.Equal([DeclarationLine(RustSource, "greet")], [.. rust.Sites.Select(site => site.Line)]);

      Assert.Equal(LspSymbolState.Ready, csharp.State);
      Assert.Equal("csharp", csharp.LanguageId);
      Assert.Equal([DeclarationLine(CSharpSource, "Greet")], [.. csharp.Sites.Select(site => site.Line)]);
      Assert.Equal(2, processes);
    }
    finally
    {
      TryDelete(root);
    }
  }

  /// <summary>Starts the harness over <paramref name="root"/> with the fake install strategy,
  ///     a synchronous UI marshal (the prompt's detection pass is marshalled), and the app's own
  ///     install root, which is the only root the daemon reads.</summary>
  private static async Task<E2E.HostHarness> StartAsync(string root)
  {
    E2E.HostHarness host = new();
    host.ClaimHostsForCurrentTest();
    host.LanguageInstallerFactory = Installer;
    host.UiMarshal = action => action();
    string workspaceId = "lsp-e2e-" + Guid.NewGuid().ToString("N");
    return await host.StartAsync(workspaceRoot: root, workspaceId: workspaceId).ConfigureAwait(true);
  }

  /// <summary>The installer the prompt and the language-servers page both read: the container's
  ///     own catalog and preference store, with an install strategy that places the in-repo stub
  ///     server where the real resolver looks.</summary>
  private static LanguageServerInstaller Installer(IServiceProvider services) =>
      new(
          services.GetRequiredService<ILanguageServerCatalog>(),
          services.GetRequiredService<IAppPreferenceStore>(),
          LspServerInstaller.DefaultRoot,
          new Dictionary<LspInstallKind, ILspInstallStrategy>
          {
            [LspInstallKind.Archive] = new StubInstallStrategy(),
            [LspInstallKind.Toolchain] = new StubInstallStrategy(),
            [LspInstallKind.NuGet] = new StubInstallStrategy(),
          });

  /// <summary>An install strategy that places the stub server at the row's real install layout
  ///     and reports Installed, so an install from the prompt needs no network.</summary>
  private sealed class StubInstallStrategy : ILspInstallStrategy
  {
    public Task<LspInstallResult> InstallAsync(LspLanguageRow row, string installRoot, CancellationToken cancellationToken)
    {
      ArgumentNullException.ThrowIfNull(row);
      ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
      _ = StubLspServer.Install(installRoot, InstallDirectory(row), row.EntryPointRelativePath, row.ServerFileName);
      return Task.FromResult(new LspInstallResult(LspInstallOutcome.Installed, row));
    }

    public string? ResolveInstalled(LspLanguageRow row, string installRoot)
    {
      ArgumentNullException.ThrowIfNull(row);
      string candidate = Path.Combine(installRoot, InstallDirectory(row),
          row.EntryPointRelativePath.Replace('/', Path.DirectorySeparatorChar), row.ServerFileName);
      return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>The directory segments the production install layout keys one row by: package
    ///     and version for a downloaded row, language and version for a toolchain row.</summary>
    private static string InstallDirectory(LspLanguageRow row) =>
        row.InstallKind == LspInstallKind.Toolchain
            ? Path.Combine(row.LanguageId, row.Version)
            : Path.Combine(row.PackageId, row.Version);
  }

  /// <summary>Waits for the session's detection pass to reach the prompt. The pass runs off the
  ///     UI thread and is not awaited by the constructor, so the wait is on the condition and is
  ///     bounded by the suite's ceiling.</summary>
  private static async Task<LanguagePromptViewModel> WaitForPromptAsync(E2E.HostHarness host)
  {
    LanguagePromptViewModel? prompt = host.Vm.LanguagePrompt;
    Assert.NotNull(prompt);
    DateTime deadline = DateTime.UtcNow + TestCeiling.Value;
    while (!prompt.IsVisible)
    {
      Assert.True(DateTime.UtcNow < deadline, "the language prompt never appeared for this workspace");
      await Task.Delay(25, Ct).ConfigureAwait(true);
    }

    return prompt;
  }

  /// <summary>One language's definition answer, over the workspace's own pipe, from a real
  ///     server process started by an in-process daemon.</summary>
  private static async Task<LspSymbolResult> DefinitionAsync(string root, string[] languages,
      string path, string symbol)
  {
    (LspSymbolResult rust, LspSymbolResult csharp, int processes) = await DefinitionsAsync(root, languages, path, symbol)
        .ConfigureAwait(true);
    _ = csharp;
    _ = processes;
    return rust;
  }

  /// <summary>Answers a definition for <paramref name="path"/> through a daemon serving the
  ///     named languages over the workspace's pipe, from real server processes, and reports how
  ///     many distinct processes served the workspace.</summary>
  private static async Task<(LspSymbolResult Rust, LspSymbolResult CSharp, int Processes)> DefinitionsAsync(
      string root, string[]? languages = null, string? path = null, string? symbol = null)
  {
    string[] served = languages ?? ["csharp", "rust"];
    string workspaceId = "lsp-e2e-serve-" + Guid.NewGuid().ToString("N");
    List<LspServerLaunch> launches = [];
    foreach (string language in served)
    {
      LspLanguageRow row = LspServerCatalog.ForLanguage(language)!;
      string? resolved = new LspServerInstaller(LspServerInstaller.DefaultRoot,
          LspServerInstaller.DefaultStrategies()).ResolveInstalled(row);
      Assert.NotNull(resolved);
      launches.Add(new LspServerLaunch(row.LanguageId, resolved, row.Version));
    }

    using LspServerSet set = await LspServerSet.StartAsync(launches, workspaceId, root, Ct).ConfigureAwait(true);
    Assert.Equal(served.Length, set.Count);
    int processes = set.Servers.Select(server => server.Connection.Process!.Id).Distinct().Count();

    using LspDaemonServer daemon = new(LspPipeNames.ForWorkspaceId(workspaceId), workspaceId, () => Task.CompletedTask);
    daemon.AttachServers(set);
    using CancellationTokenSource cts = new(TestCeiling.Value);
    Task serve = Task.Run(async () => await daemon.ServeUntilClosedAsync(cts.Token).ConfigureAwait(false), cts.Token);
    using LspPipeClient client = await ConnectAsync(workspaceId).ConfigureAwait(true);

    LspSymbolResult rust = await client.SymbolAsync("definition",
        LspUriMapper.ToUri(path ?? RustFile(root)), symbol ?? "greet", 0, 1, string.Empty, Ct)
        .ConfigureAwait(true);
    LspSymbolResult csharp = served.Contains("csharp", StringComparer.Ordinal)
        ? await WaitForReadyAsync(client, CSharpFile(root), "Greet").ConfigureAwait(true)
        : new LspSymbolResult(LspSymbolState.Unavailable, []);

    await cts.CancelAsync().ConfigureAwait(true);
    await serve.WaitAsync(TestCeiling.Value, CancellationToken.None).ConfigureAwait(true);
    return (rust, csharp, processes);
  }

  /// <summary>A C# definition, once the server has a compilation behind it. Roslyn answers
  ///     Starting until it has loaded the workspace's solution, so the load is requested first
  ///     and the query is repeated until the answer is real; the wait is on the condition and is
  ///     bounded by the suite's ceiling.</summary>
  private static async Task<LspSymbolResult> WaitForReadyAsync(LspPipeClient client, string path, string symbol)
  {
    string uri = LspUriMapper.ToUri(path);
    _ = await client.SymbolAsync("workspace-load", string.Empty, string.Empty, 0, 1, string.Empty, Ct)
        .ConfigureAwait(true);
    DateTime deadline = DateTime.UtcNow + TestCeiling.Value;
    while (true)
    {
      LspSymbolResult result = await client.SymbolAsync("definition", uri, symbol, 0, 1, string.Empty, Ct).ConfigureAwait(true);
      if (result.State == LspSymbolState.Ready && result.Sites.Count > 0)
      {
        return result;
      }

      Assert.True(DateTime.UtcNow < deadline,
          "the " + symbol + " definition never became ready: " + result.State + " " + result.Message);
      await Task.Delay(250, Ct).ConfigureAwait(true);
    }
  }

  /// <summary>Waits until the daemon accepts a connection, so a query never races its listen
  ///     backlog.</summary>
  private static async Task<LspPipeClient> ConnectAsync(string workspaceId)
  {
    string pipeName = LspPipeNames.ForWorkspaceId(workspaceId);
    for (int attempt = 0; attempt < 200; attempt++)
    {
      try
      {
        return await LspPipeClient.ConnectAsync(pipeName, workspaceId, "app-1", Ct).ConfigureAwait(true);
      }
      catch (Exception ex) when (ex is TimeoutException or IOException)
      {
        await Task.Delay(25, Ct).ConfigureAwait(true);
      }
    }

    throw new InvalidOperationException("the language-server daemon never accepted a connection");
  }

  /// <summary>The stub language-server processes that are children of this test host's daemons.
  ///     </summary>
  private static List<LiveProcess> StubServerProcesses()
  {
    WindowsProcessAncestry ancestry = new();
    List<LiveProcess> found = [];
    foreach (LiveProcess daemon in ancestry.ChildrenOf(Environment.ProcessId)
        .Where(child => string.Equals(child.ProcessName, LspDaemonLeakGuardFixture.DaemonImageName, StringComparison.OrdinalIgnoreCase)))
    {
      found.AddRange(ancestry.ChildrenOf(daemon.ProcessId)
          .Where(child => child.ProcessName.Contains("ethang-fake-lsp", StringComparison.OrdinalIgnoreCase)));
    }

    return found;
  }

  private static string WriteRustWorkspace(string root)
  {
    _ = Directory.CreateDirectory(Path.Combine(root, "src"));
    File.WriteAllText(Path.Combine(root, "Cargo.toml"),
        "[package]\nname = \"probe\"\nversion = \"0.1.0\"\nedition = \"2021\"\n");
    string file = RustFile(root);
    File.WriteAllLines(file, RustSource);
    return file;
  }

  private static string WriteCSharpWorkspace(string root)
  {
    File.WriteAllText(Path.Combine(root, "probe.csproj"),
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
    File.WriteAllText(Path.Combine(root, "probe.slnx"),
        "<Solution>\n  <Project Path=\"probe.csproj\" />\n</Solution>\n");
    string file = CSharpFile(root);
    File.WriteAllLines(file, CSharpSource);
    return file;
  }

  private static string RustFile(string root) => Path.Combine(root, "src", "lib.rs");

  private static string CSharpFile(string root) => Path.Combine(root, "Program.cs");

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
