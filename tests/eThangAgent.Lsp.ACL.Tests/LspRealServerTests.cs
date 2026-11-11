using eThangAgent.Lsp.ACL;
using eThangAgent.TestSupport;

namespace eThangAgent.Lsp.ACL.Tests;

/// <summary>The languages whose server is installed, verified and launched for real: C#
///     through the NuGet strategy, Rust through the pinned archive strategy, and TypeScript
///     through a package manager - one per install kind, so no kind's install path goes
///     unexercised. Every one reaches the network, so the class is opt-in and out of the
///     default suite. Run it with --filter-trait "Category=OptIn" and
///     ETHANG_LSP_REAL_SERVER=1.
///     <para>THE INSTALL ROOT IS A TEMP DIRECTORY, never the app cache. The app cache can
///     already hold a server another test placed there, and an install that finds its entry
///     point in place downloads and verifies nothing - which is exactly the leg this tier
///     exists to exercise.</para>
///     <para>THE SERVER IS STARTED HERE, directly, rather than through the set: what this
///     class proves is that the artifact a row names installs and that the binary it lands on
///     answers initialize. Routing a request to it is the symbol and diagnostics classes'
///     subject.</para></summary>
[Trait("Category", "OptIn")]
[Trait("Category", "RealLspServer")]
[Trait("Category", "Process")]
public class LspRealServerTests
{
  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  /// <summary>The languages the real-server tier covers, and nothing else: one per install
  ///     kind. TypeScript is here because its server is the one installed through a package
  ///     manager rather than a pinned artifact, so it is the only row whose install depends on
  ///     the layout that toolchain chooses.</summary>
  public static TheoryData<string> RealServerLanguages() => ["csharp", "rust", "typescript"];

  [Theory]
  [MemberData(nameof(RealServerLanguages))]
  public async Task EveryRealServerLanguage_InstallsVerifiedAndAnswersInitialize(string languageId)
  {
    OptInTest.SkipUnlessEnabled("ETHANG_LSP_REAL_SERVER",
        "to download and launch the real " + languageId + " server.");

    LspLanguageRow row = LspServerCatalog.ForLanguage(languageId)!;
    string installRoot = TestTempRoot.CreateDirectory("real-install-" + languageId).FullName;

    // Nothing is in place, so the artifact is fetched and verified against the row's
    // recorded checksum before anything is extracted.
    LspServerInstaller installer = new(installRoot, LspServerInstaller.DefaultStrategies());
    Assert.Null(installer.ResolveInstalled(row));
    string serverPath = await RealServerWorkspace.InstallIntoAsync(row, installRoot, Ct);

    string expectedDirectory = row.InstallKind == LspInstallKind.Toolchain
        ? Path.Combine(installRoot, row.LanguageId, row.Version)
        : Path.Combine(installRoot, row.PackageId, row.Version);
    Assert.Contains(serverPath, LspInstallLayout.EntryPointCandidates(row, expectedDirectory));
    Assert.True(File.Exists(serverPath), "the verified server must be on disk: " + serverPath);

    string workspace = TestTempRoot.CreateDirectory("real-ws-" + languageId).FullName;
    _ = RealServerWorkspace.Write(workspace, languageId);
    LspServerLaunch launch = new(languageId, serverPath, row.Version);

    using System.Diagnostics.Process process = System.Diagnostics.Process.Start(
        LspServerLauncher.BuildStartInfo(launch))!;
    try
    {
      _ = process.StandardError.ReadToEndAsync(Ct);
      LspJsonRpcTransport transport = new(
          process.StandardOutput.BaseStream,
          process.StandardInput.BaseStream,
          TimeSpan.FromSeconds(120));
      LspClient client = new(transport, new LspServerConfig(languageId, serverPath, row.Arguments, row.Extensions));
      LspRequestResult initialized = await client
          .InitializeAsync(LspUriMapper.ToUri(workspace), Ct).ConfigureAwait(true);

      Assert.True(initialized.IsSuccess,
          "the installed " + languageId + " server must answer initialize over stdio: " + initialized.ErrorMessage);
      Assert.True(client.Capabilities.Supports("textDocument/documentSymbol"),
          "a server that cannot report document symbols cannot answer a symbol query: " + languageId);
    }
    finally
    {
      if (!process.HasExited)
      {
        process.Kill(entireProcessTree: true);
      }
    }
  }

  /// <summary>A workspace that already carries its own TypeScript, at a version other than
  ///     the one this app installs, still gets a working server: the server prefers the
  ///     workspace's copy and falls back to the one installed beside it, so neither a project
  ///     pin nor this app's pin has to win. What is asserted is that the server initializes -
  ///     which copy of TypeScript it loaded is the server's choice, and pinning that here
  ///     would make this app fail when the server changes its mind.</summary>
  [Fact]
  public async Task AWorkspaceWithItsOwnTypeScript_StillInitializes()
  {
    OptInTest.SkipUnlessEnabled("ETHANG_LSP_REAL_SERVER",
        "to install and launch the real TypeScript server.");

    LspLanguageRow row = LspServerCatalog.ForLanguage("typescript")!;
    string installRoot = TestTempRoot.CreateDirectory("real-install-ts-own").FullName;
    string serverPath = await RealServerWorkspace.InstallIntoAsync(row, installRoot, Ct);

    // The workspace's own TypeScript, laid out the way npm leaves it, at a version the row
    // does not pin: the point is that the two copies disagree.
    string workspace = TestTempRoot.CreateDirectory("real-ws-ts-own").FullName;
    _ = RealServerWorkspace.Write(workspace, "typescript");
    string ownVersion = row.Companions[0].Version == "5.4.5" ? "5.5.4" : "5.4.5";
    Assert.NotEqual(row.Companions[0].Version, ownVersion);
    await RealServerWorkspace.InstallNpmPackageAsync(workspace, "typescript", ownVersion, Ct);

    using System.Diagnostics.Process process = System.Diagnostics.Process.Start(
        LspServerLauncher.BuildStartInfo(new LspServerLaunch("typescript", serverPath, row.Version)))!;
    try
    {
      _ = process.StandardError.ReadToEndAsync(Ct);
      LspJsonRpcTransport transport = new(
          process.StandardOutput.BaseStream,
          process.StandardInput.BaseStream,
          TimeSpan.FromSeconds(120));
      LspClient client = new(transport, new LspServerConfig("typescript", serverPath, row.Arguments, row.Extensions));
      LspRequestResult initialized = await client
          .InitializeAsync(LspUriMapper.ToUri(workspace), Ct).ConfigureAwait(true);

      Assert.True(initialized.IsSuccess,
          "a workspace carrying its own TypeScript must still get a working server: "
          + initialized.ErrorMessage);
    }
    finally
    {
      if (!process.HasExited)
      {
        process.Kill(entireProcessTree: true);
      }
    }
  }
}
