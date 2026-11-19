using System.Diagnostics;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.Streaming;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.TestSupport;
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
[Trait("Category", "Process")]
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
#pragma warning disable CA2007 // 'await using' cannot carry ConfigureAwait.
    await using E2E.HostHarness host = new();
#pragma warning restore CA2007
    host.ClaimHostsForCurrentTest();
    _ = await host.StartAsync();
    string ws = TestTempRoot.CreateDirectory("ethang-shutdown-ws").FullName;
    AgentSession? session = null;
    try
    {
      // The REAL factory: this launches a real ChildHost process and attaches to it.
      AgentSessionFactory factory = new(host.BuildSettings(), new AppDatabase(host.DatabasePath));
      Result<AgentSession> opened = await factory.CreateAsync(
          ws, Providers.OpenRouter, ct: TestContext.Current.CancellationToken);
      Assert.True(opened.IsSuccess, opened.Error?.Message);
      session = opened.Value;
      session.Services.GetRequiredService<SessionModelPreferences>().ModelIds = [E2E.SessionModel];

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

      await shell.ShutdownAllAsync().WaitAsync(TestCeiling.Value,
          TestContext.Current.CancellationToken).ConfigureAwait(true);

      // The host is gone. This is the assertion the issue asks for, at the seam that
      // matters: it is the same call the window's close handler makes.
      Task exit = WaitForDeathAsync(pid);
      bool died = await Task.WhenAny(exit, Task.Delay(TestCeiling.Value,
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

  /// <summary>Closing the WORKSPACE tears every agent down and leaves the process alive: two
  ///     agents in the window's one grid, two real host processes, both dead afterwards, the grid
  ///     gone, and exactly one report that the workspace is closed.</summary>
  [Fact]
  public async Task CloseWorkspace_StopsEveryHost_AndReportsOnce()
  {
#pragma warning disable CA2007 // 'await using' cannot carry ConfigureAwait.
    await using E2E.HostHarness host = new();
#pragma warning restore CA2007
    host.ClaimHostsForCurrentTest();
    _ = await host.StartAsync();
    string ws = TestTempRoot.CreateDirectory("ethang-close-workspace-ws").FullName;
    AgentSettings settings = host.BuildSettings();
    AgentSessionFactory factory = new(settings, new AppDatabase(host.DatabasePath));
    MainViewModel shell = new(createSession: null, new MainViewModelOptions
    {
      WorkspaceRoot = ws,
      PreferredProviderId = Providers.OpenRouter,
      Settings = settings,
      SessionFactory = factory,
    });
    int closed = 0;
    shell.WorkspaceClosed += (_, _) => closed++;

    try
    {
      for (int i = 0; i < 2; i++)
      {
        Result<AgentCellViewModel> added = await shell.AddAgentInWorkspaceAsync()
            .WaitAsync(TestCeiling.Value, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.True(added.IsSuccess, added.Error?.Message);
      }

      // TWO AGENTS ARE TWO HOST PROCESSES: the teardown below has two things to stop.
      List<int> pids = [.. shell.Tabs.SelectMany(t => t.Snapshot())
          .Select(c => c.Container is null ? null : HostPidOf(c.Container))
          .OfType<int>()];
      Assert.Equal(2, pids.Count);
      Assert.All(pids, pid => Assert.True(IsAlive(pid), $"host {pid} must be running before the close"));

      await shell.CloseWorkspaceCommand.ExecuteAsync(null)
          .WaitAsync(TestCeiling.Value, TestContext.Current.CancellationToken).ConfigureAwait(true);

      Assert.Empty(shell.Tabs);
      foreach (int pid in pids)
      {
        Task exit = WaitForDeathAsync(pid);
        bool died = await Task.WhenAny(exit, Task.Delay(TestCeiling.Value,
            TestContext.Current.CancellationToken)).ConfigureAwait(true) == exit;
        Assert.True(died, $"the session's host process {pid} must be dead after closing the workspace");
        Assert.False(IsAlive(pid));
      }

      Assert.Equal(1, closed);
    }
    finally
    {
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

  /// <summary>A second close-workspace request while the first runs is the same request: the
  ///     teardown is entered once and the workspace is reported closed once.</summary>
  [Fact]
  public async Task CloseWorkspace_RunsTheTeardownOnce_WhenAskedTwiceWhileItRuns()
  {
    GatedLifecycle lifecycle = new(new TestFixtures.StubStore());
    AgentSession session = SessionOver(lifecycle, @"C:\work\alpha");
    MainViewModel shell = new(createSession: (_, _) => Task.FromResult(Result.Success(session)),
        new MainViewModelOptions { WorkspaceRoot = @"C:\work\alpha" });
    int closed = 0;
    shell.WorkspaceClosed += (_, _) => closed++;

    Result<AgentTabViewModel> opened = await shell.OpenAgentInWorkspaceAsync()
        .WaitAsync(TestCeiling.Value, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(opened.IsSuccess, opened.Error?.Message);

    Task first = shell.CloseWorkspaceCommand.ExecuteAsync(null);
    await lifecycle.Entered.Task.WaitAsync(TestCeiling.Value, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    // THE SECOND REQUEST ARRIVES WHILE THE FIRST IS STILL INSIDE THE TEARDOWN.
    Task second = shell.CloseWorkspaceCommand.ExecuteAsync(null);
    await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    _ = lifecycle.Release.TrySetResult();
    await Task.WhenAll(first, second).WaitAsync(TestCeiling.Value,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal(1, lifecycle.Completions);
    Assert.Equal(1, closed);
    Assert.Empty(shell.Tabs);

    // AND THE SAME TEARDOWN IS WHAT A LATER CALLER AWAITS: one walk per shell, whatever asked.
    Assert.Same(shell.ShutdownAllAsync(), shell.ShutdownAllAsync());
  }

  /// <summary>An <see cref="AgentSession"/> over a throwaway container, carrying the lifecycle the
  ///     test watches.</summary>
  private static AgentSession SessionOver(RootSessionLifecycle lifecycle, string root)
    => new(new ServiceCollection().BuildServiceProvider(), AgentId.NewId(), new Conversation(),
        Handler: null!,
        Lifecycle: lifecycle,
        Model: ModelConfig.Create("test/model", 128, 0.1f, 8192).Value!,
        WorkspaceRoot: root,
        ProviderName: Providers.OpenRouter,
        Inbox: new BoundedAgentMailbox(),
        ChildRuntime: new TestFixtures.StubAgentRuntime());

  /// <summary>A lifecycle whose completion is held open by the test, so the teardown can be
  ///     observed while it is genuinely running.</summary>
  private sealed class GatedLifecycle(IAgentStore store) : RootSessionLifecycle(store)
  {
    private int _completions;

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Completions => Volatile.Read(ref _completions);

    public override async Task CompleteAsync(AgentId rootId, Action<string> reportError)
    {
      _ = Interlocked.Increment(ref _completions);
      _ = Entered.TrySetResult();
      await Release.Task.ConfigureAwait(false);
      await base.CompleteAsync(rootId, reportError).ConfigureAwait(false);
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
