using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using eThangAgent.Lsp.ACL;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain.Lsp;

namespace eThangAgent.Lsp.ACL.Tests;

/// <summary>Every language the catalog serves, driven end to end with no network and no
///     server process: the workspace that needs it is detected, the install strategy its row
///     names is selected and run against a fake downloader or a fake toolchain, the entry
///     point is resolved where that row's kind puts it, and a request for a file of its own
///     extension is routed to the server for that language.
///     <para>NOTHING HERE SPAWNS, which is what keeps this file in the fast loop. A row whose
///     install reaches the network or a toolchain is the real-server tier's subject, not this
///     one's: what is under test here is that the catalog, the layout, the selection and the
///     router agree about all ten rows.</para></summary>
public class LspCatalogCoverageTests
{
  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  /// <summary>Every language the catalog serves, as the data every check below is a
  ///     statement about.</summary>
  public static TheoryData<string> EveryLanguage()
  {
    TheoryData<string> data = [];
    foreach (LspLanguageRow row in LspServerCatalog.All)
    {
      data.Add(row.LanguageId);
    }

    return data;
  }

  private static string TempRoot() => TestTempRoot.CreateDirectory("lspcoverage").FullName;

  [Theory]
  [MemberData(nameof(EveryLanguage))]
  public void EveryCatalogLanguage_IsCoveredByTheseChecks(string languageId)
  {
    LspLanguageRow? row = LspServerCatalog.ForLanguage(languageId);
    Assert.NotNull(row);
    Assert.Contains(row, LspServerCatalog.All);
  }

  /// <summary>The ten languages the plan names, checked against the catalog by name, so a
  ///     row dropped from the catalog fails here rather than silently shrinking every
  ///     parameterized check in this file.</summary>
  [Fact]
  public void TheCatalogCoversExactlyTheTenPlanLanguages()
  {
    string[] planned =
    [
        "csharp", "typescript", "python", "rust", "go",
        "java", "lua", "php", "ruby", "bash",
    ];

    string[] catalogued = [.. LspServerCatalog.All.Select(row => row.LanguageId)];
    Assert.Equal(planned, catalogued);
    Assert.Equal(planned.Length, EveryLanguage().Count);
  }

  /// <summary>A workspace holding one file of a language's own extension is detected as
  ///     needing that language - the detection leg, for every row.</summary>
  [Theory]
  [MemberData(nameof(EveryLanguage))]
  public void AWorkspaceHoldingOneFileOfALanguage_DetectsThatLanguage(string languageId)
  {
    LspLanguageRow row = LspServerCatalog.ForLanguage(languageId)!;
    string root = TempRoot();
    try
    {
      File.WriteAllText(Path.Combine(root, "probe" + row.Extensions[0]), "x");
      DetectedLanguage detected = Assert.Single(LspWorkspaceLanguages.Detect(root));
      Assert.Equal(languageId, detected.LanguageId);
      Assert.Equal(1, detected.FileCount);
    }
    finally { Directory.Delete(root, true); }
  }

  /// <summary>A workspace holding only a manifest is detected too, for every row that
  ///     records one: a repository whose sources are not checked out still wants its server.
  ///     </summary>
  [Theory]
  [MemberData(nameof(EveryLanguage))]
  public void AWorkspaceHoldingOnlyAManifest_DetectsThatLanguage(string languageId)
  {
    LspLanguageRow row = LspServerCatalog.ForLanguage(languageId)!;
    if (row.ManifestNames.Count == 0)
    {
      Assert.Empty(LspWorkspaceLanguages.Detect(TempRootForNothing()));
      return;
    }

    string root = TempRoot();
    try
    {
      File.WriteAllText(Path.Combine(root, row.ManifestNames[0]), "x");
      DetectedLanguage detected = Assert.Single(LspWorkspaceLanguages.Detect(root));
      Assert.Equal(languageId, detected.LanguageId);
      Assert.True(detected.HasManifest);
      Assert.Equal(0, detected.FileCount);
    }
    finally { Directory.Delete(root, true); }
  }

  /// <summary>Every extension a row claims routes to that row, and to no other - the
  ///     routing leg, for every row.</summary>
  [Theory]
  [MemberData(nameof(EveryLanguage))]
  public void EveryExtensionALanguageClaims_RoutesBackToIt(string languageId)
  {
    LspLanguageRow row = LspServerCatalog.ForLanguage(languageId)!;
    Assert.All(row.Extensions, extension =>
        Assert.Equal(languageId, LspLanguageRouter.ForPath("src/probe" + extension)));
  }

  /// <summary>The install strategy a row selects is the one its install kind names, for
  ///     every row: a row whose kind had no strategy would throw out of the installer rather
  ///     than install.</summary>
  [Theory]
  [MemberData(nameof(EveryLanguage))]
  public async Task EveryLanguage_SelectsTheStrategyItsInstallKindNames(string languageId)
  {
    LspLanguageRow row = LspServerCatalog.ForLanguage(languageId)!;
    string root = TempRoot();
    try
    {
      RecordingStrategy recorder = new();
      LspServerInstaller installer = new(root, new Dictionary<LspInstallKind, ILspInstallStrategy>
      {
        [row.InstallKind] = recorder,
      });

      LspInstallResult result = await installer.InstallAsync(row, Ct);

      Assert.Same(row, recorder.Asked.Single());
      Assert.Equal(row.InstallKind == LspInstallKind.Toolchain
          ? LspInstallOutcome.PrerequisiteMissing
          : LspInstallOutcome.Installed,
          result.Outcome);
      Assert.Same(row, result.Row);
    }
    finally { Directory.Delete(root, true); }
  }

  /// <summary>The plan's example: every row installs against a fake downloader or a fake
  ///     toolchain and its entry point is resolved at the path its install kind dictates -
  ///     package and version for a downloaded row, language and version for a toolchain row,
  ///     with the row's own entry-point directory inside it.</summary>
  [Theory]
  [MemberData(nameof(EveryLanguage))]
  public async Task EveryLanguage_InstallsThroughAFakeAndResolvesItsEntryPointPath(string languageId)
  {
    LspLanguageRow row = LspServerCatalog.ForLanguage(languageId)!;
    string root = TempRoot();
    try
    {
      FakeInstalls installs = new();
      LspServerInstaller installer = new(root, installs.Strategies);

      LspInstallResult result = await installer.InstallAsync(row, Ct);

      Assert.Equal(LspInstallOutcome.Installed, result.Outcome);
      string expectedDirectory = row.InstallKind == LspInstallKind.Toolchain
          ? Path.Combine(root, row.LanguageId, row.Version)
          : Path.Combine(root, row.PackageId, row.Version);
      string expected = Path.Combine(
          expectedDirectory,
          row.EntryPointRelativePath.Replace('/', Path.DirectorySeparatorChar),
          row.ServerFileName);
      Assert.Equal(expected, installer.ResolveInstalled(row));
      Assert.True(File.Exists(expected), "the entry point must exist where the row says: " + expected);

      // The resolved path is what a launch is built from, so a provisioner over the same
      // installer resolves it without attempting a second install.
      LspServerProvisioner provisioner = new(_ => null, installer, root);
      LspServerLaunch launch = (await provisioner.ResolveOrInstallAsync(row, Ct))!;
      Assert.Equal(languageId, launch.LanguageId);
      Assert.Equal(expected, launch.ServerPath);
      Assert.Equal(row.Version, launch.Version);
      Assert.Equal(1, installs.InstallCount);
    }
    finally { Directory.Delete(root, true); }
  }

  /// <summary>A row's prerequisite is what its install is gated on: a fake probe reporting it
  ///     absent yields PrerequisiteMissing and runs nothing, for every row that records one.
  ///     </summary>
  [Theory]
  [MemberData(nameof(EveryLanguage))]
  public async Task APrerequisiteRow_OnAMachineWithoutIt_InstallsNothing(string languageId)
  {
    LspLanguageRow row = LspServerCatalog.ForLanguage(languageId)!;
    if (string.IsNullOrWhiteSpace(row.PrerequisiteExecutable))
    {
      Assert.Null(row.PrerequisiteExecutable);
      return;
    }

    string root = TempRoot();
    try
    {
      int runs = 0;
      LspToolchainInstallStrategy strategy = new(
          new LspPrerequisiteProbe(_ => PrerequisiteProbeResult.Missing),
          run: (_, _) => { runs++; return Task.FromResult(0); });
      LspInstallResult result = await strategy.InstallAsync(row, root, Ct);
      Assert.Equal(LspInstallOutcome.PrerequisiteMissing, result.Outcome);
      Assert.Equal(0, runs);
      Assert.False(Directory.Exists(Path.Combine(root, row.LanguageId)));
    }
    finally { Directory.Delete(root, true); }
  }

  /// <summary>A toolchain row builds the command its own toolchain takes, and every command
  ///     targets a prefix inside the app-owned root rather than a global directory.
  ///     </summary>
  [Theory]
  [MemberData(nameof(EveryLanguage))]
  public async Task AToolchainRow_BuildsACommandThatTargetsTheAppOwnedPrefix(string languageId)
  {
    LspLanguageRow row = LspServerCatalog.ForLanguage(languageId)!;
    if (row.InstallKind != LspInstallKind.Toolchain)
    {
      Assert.NotEqual(LspInstallKind.Toolchain, row.InstallKind);
      return;
    }

    string root = TempRoot();
    try
    {
      string prefix = Path.Combine(root, row.LanguageId, row.Version);
      LspToolchainCommand? observed = null;
      LspToolchainInstallStrategy strategy = new(
          new LspPrerequisiteProbe(_ => PrerequisiteProbeResult.Found("C:\\tools\\" + row.PrerequisiteExecutable + ".exe")),
          run: (command, _) =>
          {
            observed = command;
            Directory.CreateDirectory(prefix);
            File.WriteAllText(Path.Combine(prefix, row.ServerFileName), "shim");
            return Task.FromResult(0);
          });

      Assert.Equal(LspInstallOutcome.Installed, (await strategy.InstallAsync(row, root, Ct)).Outcome);
      Assert.NotNull(observed);

      // The prefix reaches the toolchain either as an argument (npm --prefix, gem
      // --install-dir) or through the environment (go GOBIN); either way the install targets
      // the app-owned directory and never a global toolchain location.
      bool targetsPrefix = observed.Arguments.Contains(prefix, StringComparer.Ordinal)
          || observed.Environment.Values.Contains(prefix, StringComparer.Ordinal);
      Assert.True(targetsPrefix, row.LanguageId + " installs outside the app-owned prefix: "
          + string.Join(" ", observed.Arguments));
      Assert.Contains(observed.Arguments, argument => argument.Contains(row.PackageId, StringComparison.Ordinal));
      Assert.Contains(observed.Arguments, argument => argument.Contains(row.Version, StringComparison.Ordinal));
    }
    finally { Directory.Delete(root, true); }
  }

  /// <summary>An archive row's artifact is verified against the row's own checksum before
  ///     anything is extracted, for every archive row: a substituted archive installs nothing.
  ///     </summary>
  [Theory]
  [MemberData(nameof(EveryLanguage))]
  public async Task AnArchiveRow_WithASubstitutedArtifact_ExtractsNothing(string languageId)
  {
    LspLanguageRow row = LspServerCatalog.ForLanguage(languageId)!;
    if (row.InstallKind != LspInstallKind.Archive)
    {
      Assert.NotEqual(LspInstallKind.Archive, row.InstallKind);
      return;
    }

    string root = TempRoot();
    try
    {
      byte[] artifact = Zip((row.EntryPointRelativePath.Length == 0
          ? row.ServerFileName
          : row.EntryPointRelativePath + "/" + row.ServerFileName, "binary"));
      LspArchiveInstallStrategy good = new(_ => Task.FromResult<byte[]?>(artifact));
      LspLanguageRow pinned = row with { Checksum = Convert.ToHexStringLower(SHA256.HashData(artifact)) };
      Assert.Equal(LspInstallOutcome.Installed, (await good.InstallAsync(pinned, root, Ct)).Outcome);
      Directory.Delete(root, true);
      Directory.CreateDirectory(root);

      LspArchiveInstallStrategy wrong = new(_ => Task.FromResult<byte[]?>(artifact));
      Assert.Equal(LspInstallOutcome.HashMismatch, (await wrong.InstallAsync(row, root, Ct)).Outcome);
      Assert.Null(wrong.ResolveInstalled(row, root));
    }
    finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
  }

  /// <summary>A NuGet row's artifact is verified against the row's base64 SHA-512, and the
  ///     entry point lands inside the row's own tools directory.</summary>
  [Fact]
  public async Task ANuGetRow_VerifiesItsBase64Sha512AndResolvesItsToolsDirectory()
  {
    LspLanguageRow row = LspServerCatalog.CSharp;
    string root = TempRoot();
    try
    {
      string entry = row.EntryPointRelativePath + "/" + row.ServerFileName;
      byte[] artifact = Zip((entry, "binary"));
      LspLanguageRow pinned = row with { Checksum = Convert.ToBase64String(SHA512.HashData(artifact)) };
      LspNuGetInstallStrategy strategy = new(_ => Task.FromResult<byte[]?>(artifact));

      Assert.Equal(LspInstallOutcome.Installed, (await strategy.InstallAsync(pinned, root, Ct)).Outcome);
      Assert.Equal(
          Path.Combine(root, row.PackageId, row.Version,
              row.EntryPointRelativePath.Replace('/', Path.DirectorySeparatorChar), row.ServerFileName),
          strategy.ResolveInstalled(pinned, root));
    }
    finally { Directory.Delete(root, true); }
  }

  /// <summary>The plan's example, at the router: all ten languages are running at once and a
  ///     request for a file of each one's own extension is answered by that language's own
  ///     server - with a fake starter, so no process exists.
  ///     </summary>
  [Fact]
  public async Task AllTenLanguages_ServeARequestForTheirOwnExtension()
  {
    string root = TempRoot();
    try
    {
      string[] languages = [.. LspServerCatalog.All.Select(row => row.LanguageId)];
      List<LspServerLaunch> launches = [.. languages.Select(languageId =>
          new LspServerLaunch(languageId, "C:\\servers\\" + languageId, "1.0.0"))];
      LspServerSet started = await LspServerSet.StartAsync(launches, "ws-all", root, new FakeStarter(), Ct);
      using LspServerSet set = WithRecordingServices(started);

      Assert.Equal(languages.Length, set.Count);
      Assert.Empty(set.Failed);
      string[] served = [.. set.Servers.Select(server => server.LanguageId)];
      Assert.Equal(languages, served);

      foreach (LspLanguageRow row in LspServerCatalog.All)
      {
        string path = Path.Combine(root, "probe" + row.Extensions[0]);
        LspSymbolResult symbol = await set.QuerySymbolAsync(
            "definition", LspUriMapper.ToUri(path), "greet", 0, 1, string.Empty, Ct);
        LspDiagnosticsResult diagnostics = await set.GetDiagnosticsAsync(path, Ct);

        Assert.Equal(row.LanguageId, symbol.LanguageId);
        Assert.Equal(row.LanguageId, Assert.Single(symbol.Sites).SymbolName);
        Assert.Equal(row.LanguageId, diagnostics.LanguageId);
        Assert.Equal(row.LanguageId, Assert.Single(diagnostics.Diagnostics).Code);
      }
    }
    finally { Directory.Delete(root, true); }
  }

  /// <summary>A language nobody installed is absent from the set, and a request for its file
  ///     is answered empty rather than by a neighbouring language's server.</summary>
  [Theory]
  [MemberData(nameof(EveryLanguage))]
  public async Task ALanguageThatIsNotInstalled_IsNotServedByAnother(string languageId)
  {
    string root = TempRoot();
    try
    {
      string[] others = [.. LspServerCatalog.All
          .Select(row => row.LanguageId)
          .Where(id => id != languageId)];
      List<LspServerLaunch> launches = [.. others.Select(id =>
          new LspServerLaunch(id, "C:\\servers\\" + id, "1.0.0"))];
      LspServerSet started = await LspServerSet.StartAsync(launches, "ws-one", root, new FakeStarter(), Ct);
      using LspServerSet set = WithRecordingServices(started);

      Assert.Null(set.ForLanguage(languageId));
      LspLanguageRow row = LspServerCatalog.ForLanguage(languageId)!;
      string path = Path.Combine(root, "probe" + row.Extensions[0]);
      Assert.Empty((await set.GetDiagnosticsAsync(path, Ct)).Diagnostics);
      Assert.Empty((await set.QuerySymbolAsync(
          "definition", LspUriMapper.ToUri(path), "greet", 0, 1, string.Empty, Ct)).Sites);
    }
    finally { Directory.Delete(root, true); }
  }

  /// <summary>The boundary the fast loop depends on: no file in this assembly that starts a
  ///     process, a pipe or a real server is missing the Process trait, and no file carrying
  ///     it is free of those things. DoctrineTests checks the whole repository against its own
  ///     marker list; this checks the assembly this step's work lives in against the markers
  ///     a language-server test actually uses.</summary>
  [Fact]
  public void EveryTestThatStartsALanguageServerCarriesTheProcessTrait()
  {
    IReadOnlyList<string> spawns = ProcessTierMarkers.Spawns;
    string trait = ProcessTierMarkers.Trait;

    // This file names no marker itself - the list is data in ProcessTierMarkers, a file that
    // holds no test - so it needs no exemption and its own text is checked like every other's.
    List<string> missing = [];
    List<string> unearned = [];
    foreach (string file in Directory.EnumerateFiles(AssemblyDirectory(), "*.cs"))
    {
      string text = File.ReadAllText(file);
      if (!text.Contains("[Fact", StringComparison.Ordinal)
          && !text.Contains("[Theory", StringComparison.Ordinal))
      {
        continue;
      }

      bool starts = spawns.Any(marker => text.Contains(marker, StringComparison.Ordinal));
      bool carries = text.Contains(trait, StringComparison.Ordinal);
      if (starts && !carries)
      {
        missing.Add(Path.GetFileName(file));
      }
      else if (!starts && carries)
      {
        unearned.Add(Path.GetFileName(file));
      }
    }

    Assert.True(missing.Count == 0,
        "tests starting a language server without the Process trait (they would run in the fast loop): "
        + string.Join(", ", missing));
    Assert.True(unearned.Count == 0,
        "tests carrying the Process trait that start nothing (they would leave the fast loop for no reason): "
        + string.Join(", ", unearned));
  }

  /// <summary>The real-server tier is exactly the two languages the plan names, so a third
  ///     language's server is never required by a test run.</summary>
  [Fact]
  public void TheRealServerTierCoversExactlyCSharpAndRust()
  {
    string[] realServerFiles =
    [
        "LspRealServerTests.cs",
        "LspRealServerSymbolTests.cs",
        "LspRealServerDiagnosticsTests.cs",
    ];

    HashSet<string> languages = new(StringComparer.Ordinal);
    foreach (string file in realServerFiles)
    {
      string text = File.ReadAllText(Path.Combine(AssemblyDirectory(), file));
      foreach (LspLanguageRow row in LspServerCatalog.All)
      {
        if (text.Contains("\"" + row.LanguageId + "\"", StringComparison.Ordinal)
            || text.Contains("LspServerCatalog.ForLanguage(\"" + row.LanguageId + "\")", StringComparison.Ordinal)
            || text.Contains("LspServerCatalog." + CSharpMember(row.LanguageId), StringComparison.Ordinal))
        {
          _ = languages.Add(row.LanguageId);
        }
      }
    }

    string[] covered = [.. languages.Order(StringComparer.Ordinal)];
    Assert.Equal(["csharp", "rust"], covered);
  }

  /// <summary>The catalog member a row is reached by when it has one, so a test that names
  ///     the C# row through <c>LspServerCatalog.CSharp</c> counts as covering it.</summary>
  private static string CSharpMember(string languageId)
    => string.Equals(languageId, "csharp", StringComparison.Ordinal) ? "CSharp" : "\u0000";

  private static string TempRootForNothing() => TestTempRoot.CreateDirectory("lspcoverage-empty").FullName;

  private static string AssemblyDirectory()
  {
    DirectoryInfo? dir = new(Path.GetDirectoryName(typeof(LspCatalogCoverageTests).Assembly.Location)!);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "eThangAgent.slnx")))
    {
      dir = dir.Parent;
    }

    Assert.NotNull(dir);
    return Path.Combine(dir.FullName, "tests", "eThangAgent.Lsp.ACL.Tests");
  }

  private static byte[] Zip(params (string Name, string Content)[] entries)
  {
    using MemoryStream stream = new();
    using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
    {
      foreach ((string name, string content) in entries)
      {
        ZipArchiveEntry entry = archive.CreateEntry(name);
        using Stream body = entry.Open();
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        body.Write(bytes, 0, bytes.Length);
      }
    }

    return stream.ToArray();
  }

  /// <summary>An install strategy that answers Installed and writes the row's entry point at
  ///     the layout its kind dictates, so the resolved path is the production path.</summary>
  private sealed class FakeInstallStrategy : ILspInstallStrategy
  {
    public int Installs { get; private set; }

    public Task<LspInstallResult> InstallAsync(LspLanguageRow row, string installRoot, CancellationToken cancellationToken)
    {
      Installs++;
      string directory = LspInstallLayout.DirectoryFor(row, installRoot);
      string entry = LspInstallLayout.EntryPoint(row, directory);
      _ = Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
      File.WriteAllText(entry, "shim");
      return Task.FromResult(new LspInstallResult(LspInstallOutcome.Installed, row));
    }

    public string? ResolveInstalled(LspLanguageRow row, string installRoot)
      => LspInstallLayout.InstalledEntryPoint(row, installRoot);
  }

  /// <summary>One fake strategy per install kind over one shared counter, so a test can
  ///     observe both how many installs ran and which kind served the row.</summary>
  private sealed class FakeInstalls
  {
    private readonly FakeInstallStrategy _strategy = new();

    public FakeInstalls() => Strategies = new Dictionary<LspInstallKind, ILspInstallStrategy>
    {
      [LspInstallKind.NuGet] = _strategy,
      [LspInstallKind.Archive] = _strategy,
      [LspInstallKind.Toolchain] = _strategy,
    };

    public IReadOnlyDictionary<LspInstallKind, ILspInstallStrategy> Strategies { get; }

    public int InstallCount => _strategy.Installs;
  }

  /// <summary>A strategy that records the row it was handed and answers the outcome the
  ///     test named, so the installer's selection is what is observed.</summary>
  private sealed class RecordingStrategy : ILspInstallStrategy
  {
    public List<LspLanguageRow> Asked { get; } = [];

    public Task<LspInstallResult> InstallAsync(LspLanguageRow row, string installRoot, CancellationToken cancellationToken)
    {
      Asked.Add(row);
      LspInstallOutcome outcome = row.InstallKind == LspInstallKind.Toolchain
          ? LspInstallOutcome.PrerequisiteMissing
          : LspInstallOutcome.Installed;
      return Task.FromResult(new LspInstallResult(outcome, row));
    }

    public string? ResolveInstalled(LspLanguageRow row, string installRoot) => null;
  }

  private sealed class FakeStarter : ILspServerStarter
  {
    public Task<LspServerConnection> StartAsync(LspServerLaunch launch, string workspaceRoot, CancellationToken ct)
    {
      MemoryStream input = new();
      MemoryStream output = new();
      LspJsonRpcTransport transport = new(input, output, TimeSpan.FromSeconds(2));
      LspClient client = new(transport, new LspServerConfig(
          launch.LanguageId, launch.ServerPath, [], LspServerCatalog.ForLanguage(launch.LanguageId)!.Extensions));
      return Task.FromResult(new LspServerConnection(transport, client, process: null));
    }
  }

  private sealed class FakeDiagnostics(string languageId) : ILspDiagnosticsQuery
  {
    private readonly string _languageId = languageId;

    public Task<LspDiagnosticsResult> PullAsync(string uri, CancellationToken ct = default)
      => Task.FromResult(new LspDiagnosticsResult(
          LspDiagnosticsState.Ready,
          [new LspDiagnostic(uri, 0, 0, 0, 1, _languageId, "reported by " + _languageId, 1)],
          _languageId));

    public void BeginRestart()
    {
    }

    public Task CompleteRestartAsync() => Task.CompletedTask;
  }

  private sealed class FakeSymbols(string languageId) : ILspSymbolQuery
  {
    private readonly string _languageId = languageId;

    public Task<LspSymbolResult> ExecuteAsync(
        string action, string uri, string symbolName, int occurrence, int depth, string newName,
        CancellationToken ct = default)
      => Task.FromResult(new LspSymbolResult(
          LspSymbolState.Ready,
          [new LspSymbolSite(uri, 1, 2, _languageId)],
          occurrence, 1, LanguageId: _languageId));
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
}
