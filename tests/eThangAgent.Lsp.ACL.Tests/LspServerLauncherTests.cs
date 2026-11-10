using System.Diagnostics;
using eThangAgent.Lsp.ACL;

namespace eThangAgent.Lsp.ACL.Tests;

[Trait("Category", "Process")]
public class LspServerLauncherTests
{
  [Fact]
  public void BuildStartInfo_SpeaksStdioOverRedirectedPipes()
  {
    LspServerLaunch launch = new("csharp", "C:\\servers\\roslyn-language-server.exe", "5.13.0");
    ProcessStartInfo psi = LspServerLauncher.BuildStartInfo(launch);
    Assert.Equal(launch.ServerPath, psi.FileName);
    Assert.Equal(LspServerCatalog.ForLanguage("csharp")!.Arguments, psi.ArgumentList);
    Assert.True(psi.RedirectStandardInput);
    Assert.True(psi.RedirectStandardOutput);
    Assert.False(psi.UseShellExecute);
  }

  [Fact]
  public void BuildStartInfo_NeverPassesLegacyLanguageserverFlag()
  {
    ProcessStartInfo psi = LspServerLauncher.BuildStartInfo(
        new LspServerLaunch("csharp", "C:\\servers\\roslyn-language-server.exe", "5.13.0"));
    Assert.DoesNotContain("--languageserver", psi.ArgumentList);
  }

  /// <summary>The command line is the catalog row's own arguments: the stdio flag a server
  ///     that takes one accepts, and nothing at all for a row with no arguments.</summary>
  [Fact]
  public void BuildStartInfo_UsesTheRowsOwnArguments()
  {
    ProcessStartInfo typescript = LspServerLauncher.BuildStartInfo(
        new LspServerLaunch("typescript", "C:\\servers\\typescript-language-server.cmd", "6.0.3"));
    ProcessStartInfo rust = LspServerLauncher.BuildStartInfo(
        new LspServerLaunch("rust", "C:\\servers\\rust-analyzer.exe", "2026-11-09"));

    Assert.Equal(["--stdio"], typescript.ArgumentList);
    Assert.Empty(rust.ArgumentList);
  }
}
