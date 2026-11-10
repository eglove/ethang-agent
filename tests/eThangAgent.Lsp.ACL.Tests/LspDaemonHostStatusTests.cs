using eThangAgent.Lsp.ACL;
using eThangAgent.TestSupport;

namespace eThangAgent.Lsp.ACL.Tests;

/// <summary>The host owns the daemon process, so it owns the only reader of the daemon's
///     stdout. Its monitor is the state a status bar binds: present from construction (a
///     reader can subscribe before any daemon exists) and unchanged until the daemon says
///     something.</summary>
public class LspDaemonHostStatusTests
{
  [Fact]
  public void TheHostExposesItsMonitor_WhichStartsUnknown()
  {
    using LspDaemonHost host = new("ws-status", Directory.GetCurrentDirectory(), "app-1",
        () => "C:\\nowhere\\eThangAgent.Lsp.Host.exe");

    Assert.NotNull(host.ServerStatus);
    Assert.Equal(LspServerInstallState.Unknown, host.ServerStatus.State);
  }

  [Fact]
  public void TheHost_StartsUnstarted()
  {
    using LspDaemonHost host = new("ws-status", Directory.GetCurrentDirectory(), "app-1",
        () => "C:\\nowhere\\eThangAgent.Lsp.Host.exe");

    Assert.False(host.IsStarted);
  }

  [Fact]
  public async Task StartAsync_MarksTheHostStarted_AndIsIdempotent()
  {
    string root = TestTempRoot.CreateDirectory("lsp-start").FullName;
    string workspaceId = "ws-start-" + Guid.NewGuid().ToString("N");
    try
    {
      await File.WriteAllTextAsync(Path.Combine(root, "a.cs"), "public class A { }", TestContext.Current.CancellationToken).ConfigureAwait(true);

      using LspDaemonHost host = new(workspaceId, root, "app-1",
          () => throw new InvalidOperationException("no daemon process to start in this test"));

      Assert.False(host.IsStarted);
      _ = await host.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(host.IsStarted);

      _ = await host.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(host.IsStarted);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  [Fact]
  public async Task AWorkspaceWhoseOnlyLanguageIsNotCSharp_IsServed()
  {
    string root = TestTempRoot.CreateDirectory("lsp-non-csharp").FullName;
    try
    {
      await File.WriteAllTextAsync(Path.Combine(root, "main.lua"), "print('hi')",
          TestContext.Current.CancellationToken).ConfigureAwait(true);

      using LspDaemonHost host = new("ws-lua", root, "app-1",
          () => throw new InvalidOperationException("no daemon process to start in this test"));

      Assert.Equal(["lua"], [.. host.Languages.Select(detected => detected.LanguageId)]);
      Assert.True(host.HasLanguages,
          "a Lua workspace holds a language this app serves, so the daemon must start for it.");

      _ = await host.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

      Assert.True(host.IsStarted);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  [Fact]
  public async Task AWorkspaceHoldingNoLanguage_StartsNothing()
  {
    string root = TestTempRoot.CreateDirectory("lsp-no-language").FullName;
    try
    {
      await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "# notes",
          TestContext.Current.CancellationToken).ConfigureAwait(true);

      using LspDaemonHost host = new("ws-docs", root, "app-1",
          () => throw new InvalidOperationException("a code-free workspace must start no daemon"));

      Assert.Empty(host.Languages);
      Assert.False(host.HasLanguages);
      Assert.Null(await host.EnsureConnectedAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
      Assert.Null(host.Client);
      Assert.Null(host.DaemonProcessId);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  [Fact]
  public void TwoHosts_NeverShareAMonitor()
  {
    using LspDaemonHost first = new("ws-a", Directory.GetCurrentDirectory(), "app-1",
        () => "C:\\nowhere\\eThangAgent.Lsp.Host.exe");
    using LspDaemonHost second = new("ws-b", Directory.GetCurrentDirectory(), "app-1",
        () => "C:\\nowhere\\eThangAgent.Lsp.Host.exe");

    Assert.NotSame(first.ServerStatus, second.ServerStatus);
  }
}
