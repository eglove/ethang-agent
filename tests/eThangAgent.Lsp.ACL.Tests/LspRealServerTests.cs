using eThangAgent.Lsp.ACL;
using eThangAgent.TestSupport;

namespace eThangAgent.Lsp.ACL.Tests;

/// <summary>Exercises the real downloaded server, opt-in so the default suite stays
/// deterministic. Run with --filter-trait "Category=OptIn" and ETHANG_LSP_REAL_SERVER=1.</summary>
[Trait("Category", "OptIn")]
[Trait("Category", "RealLspServer")]
public class LspRealServerTests
{
  [Fact]
  public async Task InstalledServer_AnswersInitializeOverStdio()
  {
    OptInTest.SkipUnlessEnabled("ETHANG_LSP_REAL_SERVER", "to run against the real server.");

    LspServerInstaller installer = new(
        LspServerInstaller.DefaultRoot,
        package => LspServerDownloader.DownloadAsync(package, TestContext.Current.CancellationToken));
    LspInstallResult installed = await installer.InstallAsync(
        LspServerCatalog.CSharp, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.Equal(LspInstallOutcome.Installed, installed.Outcome);

    string? serverPath = installer.ResolveInstalled(LspServerCatalog.CSharp);
    Assert.NotNull(serverPath);
    LspServerLaunch launch = new("csharp", serverPath, LspServerCatalog.CSharp.Version);

    using System.Diagnostics.Process process = System.Diagnostics.Process.Start(
        LspServerLauncher.BuildStartInfo(launch))!;
    try
    {
      _ = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
      LspJsonRpcTransport transport = new(
          process.StandardOutput.BaseStream,
          process.StandardInput.BaseStream,
          TimeSpan.FromSeconds(120));
      LspClient client = new(transport, LspLanguageDetector.CSharpConfig(serverPath));
      LspRequestResult initialized = await client.InitializeAsync(
          LspUriMapper.ToUri(TestTempRoot.Root), TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(initialized.IsSuccess, "the installed server must answer initialize over stdio: " + initialized.ErrorMessage);
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
