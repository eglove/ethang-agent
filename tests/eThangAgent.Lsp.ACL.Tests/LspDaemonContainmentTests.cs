using System.Diagnostics;
using eThangAgent.Lsp.ACL;
using eThangAgent.TestSupport;
using eThangAgent.Windows.ACL;

namespace eThangAgent.Lsp.ACL.Tests;

/// <summary>The containment guarantee the daemon host makes: a daemon it starts is assigned
///     to a kill-on-close job, so the kernel reaps it when the app exits or dies, and a
///     disposed host leaves no daemon behind.
///     <para>WHY IT EXISTS: the explicit kill in <see cref="LspDaemonConnection"/> covers a
///     clean shutdown and nothing else. An app that is killed outright - task manager, a
///     crash, a hard power event - runs no cleanup code, so without the job the daemon
///     outlives its app holding a pipe and a language server that nobody can find except by
///     image name.</para></summary>
[Trait("Category", "Process")]
public class LspDaemonContainmentTests
{
  /// <summary>A started daemon is inside the host's own kill-on-close job. Asserted against
  ///     the host's handle rather than job membership in general, because a child inherits its
  ///     parent's job: 'in some job' would pass with no attach at all. The reap itself is
  ///     pinned against a real child in <c>HostContainmentTests</c>.</summary>
  [Fact]
  public async Task StartedDaemon_IsContainedByAKillOnCloseJob()
  {
    string root = TestTempRoot.CreateDirectory("lsp-contained").FullName;
    string workspaceId = "contained-" + Guid.NewGuid().ToString("N");
    try
    {
      await File.WriteAllTextAsync(Path.Combine(root, "a.cs"), "class A { }", TestContext.Current.CancellationToken).ConfigureAwait(true);
      await using LspDaemonHost host = new(workspaceId, root, "app-1", () => LspDaemonExeLocator.CreateDefault().Resolve());

      LspPipeClient? client = await host.EnsureConnectedAsync(TestContext.Current.CancellationToken)
          .WaitAsync(TestCeiling.Value, TestContext.Current.CancellationToken).ConfigureAwait(true);

      Assert.NotNull(client);
      int daemonPid = Assert.IsType<int>(host.DaemonProcessId);
      using Process daemon = Process.GetProcessById(daemonPid);
      Assert.True(JobObject.IsInJob(host.DaemonJobHandle, daemon),
          "the daemon the host started is not in the host's job, so nothing reaps it when the app dies");
    }
    finally
    {
      DeleteWorkspace(root);
    }
  }

  /// <summary>A disposed host owns no daemon: the clean path, which the explicit kill covers
  ///     and the job must not be relied on for.</summary>
  [Fact]
  public async Task DisposedHost_LeavesNoDaemon()
  {
    string root = TestTempRoot.CreateDirectory("lsp-reaped").FullName;
    string workspaceId = "reaped-" + Guid.NewGuid().ToString("N");
    try
    {
      await File.WriteAllTextAsync(Path.Combine(root, "a.cs"), "class A { }", TestContext.Current.CancellationToken).ConfigureAwait(true);
      LspDaemonHost host = new(workspaceId, root, "app-1", () => LspDaemonExeLocator.CreateDefault().Resolve());
      int daemonPid;
      try
      {
        LspPipeClient? client = await host.EnsureConnectedAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TestCeiling.Value, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.NotNull(client);
        daemonPid = Assert.IsType<int>(host.DaemonProcessId);
      }
      finally
      {
        await host.DisposeAsync().ConfigureAwait(true);
      }

      Assert.Null(host.DaemonProcessId);
      Assert.True(await ExitsAsync(daemonPid).ConfigureAwait(true),
          "the daemon outlived the host that owned it");
    }
    finally
    {
      DeleteWorkspace(root);
    }
  }

  /// <summary>True once the process is gone. A kill is a request the kernel settles, so the
  ///     fact is polled under the test ceiling rather than asserted after a fixed sleep.
  ///     </summary>
  private static async Task<bool> ExitsAsync(int processId)
  {
    using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
        TestContext.Current.CancellationToken);
    cts.CancelAfter(TestCeiling.Value);
    while (!cts.IsCancellationRequested)
    {
      try
      {
        using Process process = Process.GetProcessById(processId);
        if (process.HasExited)
        {
          return true;
        }
      }
      catch (ArgumentException)
      {
        return true;
      }

      try
      {
        await Task.Delay(50, cts.Token).ConfigureAwait(true);
      }
      catch (OperationCanceledException)
      {
        return false;
      }
    }

    return false;
  }

  /// <summary>Removes a temp workspace, tolerating the brief window in which a just-killed
  ///     daemon still holds a handle in it.</summary>
  private static void DeleteWorkspace(string root)
  {
    for (int attempt = 0; attempt < 20; attempt++)
    {
      try
      {
        Directory.Delete(root, true);
        return;
      }
      catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
      {
        Thread.Sleep(100);
      }
    }
  }
}
