using eThangAgent.Lsp.ACL;

namespace eThangAgent.Lsp.ACL.Tests;

// Issue #361: daemon executable discovery. The copy shipped next to the app wins, the
// repo's live build output is the dev fallback, and the deployed path is returned when
// neither exists so a caller's not-found error names where the daemon is expected.
public class LspDaemonExeLocatorTests
{
  [Fact]
  public void Resolve_PrefersCopyShippedNextToApp()
  {
    string appDir = @"C:\app";
    string deployed = Path.Combine(appDir, LspDaemonExeLocator.ExeName);
    LspDaemonExeLocator locator = new(p => p == deployed, @"C:\repo", appDir);

    Assert.Equal(deployed, locator.Resolve());
  }

  [Fact]
  public void Resolve_FallsBackToRepoBuildOutput()
  {
    string expected = Path.Combine(@"C:\repo", "src", "eThangAgent.Lsp.Host", "bin", "Debug", "net10.0-windows", LspDaemonExeLocator.ExeName);
    LspDaemonExeLocator locator = new(p => p == expected, @"C:\repo", @"C:\app");

    Assert.Equal(expected, locator.Resolve());
  }

  [Fact]
  public void Resolve_NothingBuilt_ReturnsDeployedPath()
  {
    LspDaemonExeLocator locator = new(_ => false, @"C:\repo", @"C:\app");

    Assert.Equal(Path.Combine(@"C:\app", LspDaemonExeLocator.ExeName), locator.Resolve());
  }

  [Fact]
  public void Resolve_PublishedAppWithoutRepo_ReturnsDeployedPath()
  {
    LspDaemonExeLocator locator = new(_ => false, null, @"C:\app");

    Assert.Equal(Path.Combine(@"C:\app", LspDaemonExeLocator.ExeName), locator.Resolve());
  }
}
