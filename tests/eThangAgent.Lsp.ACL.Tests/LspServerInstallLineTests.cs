using eThangAgent.Lsp.ACL;

namespace eThangAgent.Lsp.ACL.Tests;

/// <summary>The daemon announces what it is doing with the language server on its own
///     stdout, and <see cref="LspDaemonHost"/> is the only reader of that stream. These
///     pin the line-to-state mapping: the status bar can only be honest if the parse is.</summary>
public class LspServerInstallLineTests
{
  [Theory]
  [InlineData("server-resolving ws-abc", LspServerInstallState.Downloading)]
  [InlineData("server-ready 5.13.0-1.26514.10", LspServerInstallState.Ready)]
  [InlineData("server-unavailable", LspServerInstallState.Unavailable)]
  public void AnnouncedLines_MapToTheirState(string line, LspServerInstallState expected)
  {
    Assert.True(LspServerInstallLine.TryParse(line, out LspServerInstallState state, out _));
    Assert.Equal(expected, state);
  }

  [Fact]
  public void ReadyLine_CarriesTheVersionAsDetail()
  {
    Assert.True(LspServerInstallLine.TryParse("server-ready 5.13.0-1.26514.10",
        out _, out string detail));
    Assert.Equal("5.13.0-1.26514.10", detail);
  }

  [Theory]
  [InlineData("daemon-starting ethang-lsp-0000")]
  [InlineData("daemon-ready ws-abc")]
  [InlineData("")]
  [InlineData("   ")]
  [InlineData("server-ready")]
  public void EveryOtherLine_IsNotAStateAnnouncement(string line)
      => Assert.False(LspServerInstallLine.TryParse(line, out _, out _));

  [Fact]
  public void NullLine_IsRejectedRatherThanThrowing()
      => Assert.False(LspServerInstallLine.TryParse(null, out _, out _));
}
