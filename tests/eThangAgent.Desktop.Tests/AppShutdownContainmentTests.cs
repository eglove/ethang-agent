using System.Diagnostics;
using eThangAgent.Composition;
using eThangAgent.Desktop.Streaming;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>Closing the app kills everything (issue #274). The reported defect: the window-close
///     handler only completed each root session gracefully and never disposed the containers,
///     so every conversation host process survived the app - `RemoteHostSupervisor.DisposeAsync`
///     never ran. The fix routes window close through <see cref="MainViewModel.ShutdownAllAsync"/>,
///     which closes each tab exactly as the tab-close button does.
///     <para>WHY THIS SHAPE. The issue asked for a test that drives the real window-close
///     sequence headless; that is not achievable in-process (headless Avalonia provides no
///     classic-desktop lifetime, and windows shown against a standalone lifetime never
///     register with it - see StartupShutdownModeTests). So the guarantee is split in two:
///     THIS test drives the teardown the close handler calls, over a REAL composed session
///     with a REAL host process, and asserts the process is gone; the kernel half (a crash
///     reaping the host via the kill-on-close job) is pinned by
///     eThangAgent.Composition.Tests.HostContainmentTests.</para></summary>
[Collection("Desktop E2E")]
public class AppShutdownContainmentTests
{
  /// <summary>The host process a session owns, or null when the session runs in-process.</summary>
  private static int? HostPidOf(AgentSession session) =>
      session.Services.GetService<RemoteHostSupervisor>()?.HostProcessId;

  private static bool IsAlive(int pid)
  {
    try
    {
      using Process p = Process.GetProcessById(pid);
      return !p.HasExited;
    }
    catch (ArgumentException)
    {
      return false; // no such process: exactly the end state this test wants
    }
  }

  /// <summary>The whole point: after the app-wide shutdown, no host process this app
  ///     started is still running.</summary>
  [Fact]
  public async Task ShutdownAll_KillsEverySessionsHostProcess()
  {
    using E2E.HostHarness host = new();
    _ = await host.StartAsync();
    string ws = Directory.CreateTempSubdirectory("ethang-shutdown-ws").FullName;
    AgentSession? session = null;
    try
    {
      // The REAL factory: this launches a real ChildHost process and attaches to it.
      AgentSessionFactory factory = new(host.BuildSettings(), new AppDatabase(host.DatabasePath));
      Result<AgentSession> opened = await factory.CreateAsync(
          ws, Providers.OpenRouter, ct: TestContext.Current.CancellationToken);
      Assert.True(opened.IsSuccess, opened.Error?.Message);
      session = opened.Value;
      session.Services.GetRequiredService<SessionModelPreferences>().ModelId = E2E.SessionModel;

      int pid = HostPidOf(session)
          ?? throw new InvalidOperationException("the session must run in its own host process");
      Assert.True(IsAlive(pid), "the session's host must be running before shutdown");

      // The shell the window-close handler drives.
      AgentSessionViewModel? vmRef = null;
      async Task Sink(UiStreamEvent evt) =>
          await (vmRef ?? throw new InvalidOperationException("sink before view-model init"))
              .ApplyUiStreamEventAsync(evt).ConfigureAwait(true);
      MainViewModel shell = await MainViewModel.ForPrebuiltSessionAsync(session, Sink);
      vmRef = shell.Tabs[0].ViewModel;

      await shell.ShutdownAllAsync().WaitAsync(TimeSpan.FromSeconds(60),
          TestContext.Current.CancellationToken).ConfigureAwait(true);

      // The host is gone. This is the assertion the issue asks for, at the seam that
      // matters: it is the same call the window's close handler makes.
      Task exit = WaitForDeathAsync(pid);
      bool died = await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(30),
          TestContext.Current.CancellationToken)).ConfigureAwait(true) == exit;
      Assert.True(died, $"the session's host process {pid} must be dead after app shutdown");
      Assert.False(IsAlive(pid));

      // The tab is gone too: shutdown closes every tab, so nothing is left holding a
      // container whose host has been killed.
      Assert.Empty(shell.Tabs);
    }
    finally
    {
      // The session is disposed by ShutdownAllAsync on the success path; this covers the
      // failure path so a failing assertion above cannot strand a host (the assembly's
      // leak guard would catch it, but failing here with the real cause is better).
      if (session is not null)
      {
        try
        {
          await session.Services.DisposeAsync().ConfigureAwait(true);
        }
        catch (ObjectDisposedException)
        {
          // already disposed by the shutdown under test
        }
      }

      try
      {
        Directory.Delete(ws, recursive: true);
      }
      catch (IOException)
      {
        // best effort: a still-draining host may pin the directory
      }
    }
  }

  /// <summary>Polls the OS for the process's death: HasExited on a disposed Process handle
  ///     throws, and the supervisor owns that handle, so this asks the OS by id instead.
  ///     Bounded by the caller.</summary>
  private static async Task WaitForDeathAsync(int pid)
  {
    while (IsAlive(pid))
    {
      await Task.Delay(TimeSpan.FromMilliseconds(50),
          TestContext.Current.CancellationToken).ConfigureAwait(true);
    }
  }
}
