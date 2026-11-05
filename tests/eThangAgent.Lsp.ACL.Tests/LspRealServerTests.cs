using eThangAgent.Lsp.ACL;

namespace eThangAgent.Lsp.ACL.Tests;

/// <summary>Exercises the real downloaded server, opt-in by trait so the default suite
/// stays deterministic. Run with --filter-trait "Category=RealLspServer".</summary>
public class LspRealServerTests
{
  [Fact]
  [Trait("Category", "RealLspServer")]
  public async Task InstalledServer_AnswersInitializeOverStdio()
  {
    if (Environment.GetEnvironmentVariable("ETHANG_LSP_REAL_SERVER") is not "1")
    {
      Assert.Skip("Opt-in: set ETHANG_LSP_REAL_SERVER=1 to run against the real server.");
    }

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
          LspUriMapper.ToUri(Path.GetTempPath()), TestContext.Current.CancellationToken).ConfigureAwait(true);
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
