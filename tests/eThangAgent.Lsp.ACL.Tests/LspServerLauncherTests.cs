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
    Assert.Contains("--stdio", psi.ArgumentList);
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
}
