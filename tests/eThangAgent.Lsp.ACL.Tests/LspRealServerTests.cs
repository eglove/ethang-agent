using eThangAgent.Lsp.ACL;
using eThangAgent.TestSupport;

namespace eThangAgent.Lsp.ACL.Tests;

/// <summary>The two languages whose server is installed, verified and launched for real: C#
///     through the NuGet strategy and Rust through the pinned archive strategy. Both reach
///     the network, so the class is opt-in and out of the default suite. Run it with
///     --filter-trait "Category=OptIn" and ETHANG_LSP_REAL_SERVER=1.
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

  /// <summary>The languages the real-server tier covers, and nothing else: a third
  ///     language's artifact is never required by a test run. TypeScript is here because its
  ///     server is the one installed through a package manager rather than a pinned artifact,
  ///     so it is the only row whose install depends on the layout that toolchain chooses.</summary>
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
}
