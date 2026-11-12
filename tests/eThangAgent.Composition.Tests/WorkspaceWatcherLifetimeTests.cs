using eThangAgent.AgentDomain;
using eThangAgent.FileSystem.ACL;
using eThangAgent.ModelDomain;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;
using LspDaemonExePathOverride = eThangAgent.Lsp.ACL.LspDaemonExePathOverride;

namespace eThangAgent.Composition.Tests;

/// <summary>Issue #381: the session container OWNS a hold on its workspace's watcher, so
///     disposing the container is what stops the walk. Before this, the watcher was created
///     inside a closure the container never saw, so every session ever opened in the process
///     left two loops walking and hashing the whole tree for the life of the app.
///     <para>The composed container is real, which is what earns the Process trait.</para>
///     </summary>
[Trait("Category", "Process")]
public class WorkspaceWatcherLifetimeTests
{
  private static AgentSettings Settings() => new(
      new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
      new AgentRunOptions(null, 2));

  private static ServiceProvider Compose(TestAppDatabase db, string root)
  {
    ServiceProvider services = new ServiceCollection()
        .AddEThangAgentCore(Settings(), Providers.OpenRouter,
            ModelConfig.Create("test/model", 512, 0.5f, 8192).Value!,
            new AgentHostOptions(FixedWorkspaceContext.ForPath(root), new UnrootedPathResolver()),
            db.Database)
        .BuildServiceProvider();
    services.GetRequiredService<LspDaemonExePathOverride>().Path =
        Path.Combine(root, "no-such-daemon", "eThangAgent.Lsp.Host.exe");
    return services;
  }

  private static DirectoryInfo Workspace(params string[] files)
  {
    DirectoryInfo directory = TestTempRoot.CreateDirectory("ws-watch-lifetime");
    foreach (string file in files)
    {
      string path = Path.Combine(directory.FullName, file);
      _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      File.WriteAllText(path, string.Empty);
    }

    return directory;
  }

  /// <summary>Two containers over ONE workspace resolve the same watcher, because the
  /// resource belongs to the directory. This is the arithmetic the issue measured: cost
  /// must be linear in workspaces, not in sessions.</summary>
  [Fact]
  public async Task TwoContainersOverOneWorkspace_ShareOneWatcher()
  {
    DirectoryInfo workspace = Workspace("a.cs");
    try
    {
      using TestAppDatabase db = TestAppDatabase.Create();
      await using ServiceProvider first = Compose(db, workspace.FullName);
      await using ServiceProvider second = Compose(db, workspace.FullName);

      WorkspaceWatcherLease one = first.GetRequiredService<WorkspaceWatcherLease>();
      WorkspaceWatcherLease two = second.GetRequiredService<WorkspaceWatcherLease>();

      Assert.Same(one.Watcher, two.Watcher);
    }
    finally
    {
      Directory.Delete(workspace.FullName, true);
    }
  }

  /// <summary>THE DEFECT ITSELF: disposing the container must stop the walk. A container
  /// that is the last holder leaves no running watcher behind it.</summary>
  [Fact]
  public async Task DisposingTheLastContainer_StopsTheWatcher()
  {
    DirectoryInfo workspace = Workspace("a.cs");
    try
    {
      using TestAppDatabase db = TestAppDatabase.Create();
      WorkspaceFileWatcher watcher;
      ServiceProvider services = Compose(db, workspace.FullName);
      try
      {
        WorkspaceWatcherLease lease = services.GetRequiredService<WorkspaceWatcherLease>();
        lease.EnsureStarted();
        watcher = lease.Watcher;
        Assert.True(watcher.IsStarted);
      }
      finally
      {
        await services.DisposeAsync();
      }

      // The hold is gone, so a fresh hold over the same root is a NEW watcher - the stopped
      // one was forgotten rather than handed to the next session.
      await using ServiceProvider next = Compose(db, workspace.FullName);
      Assert.NotSame(watcher, next.GetRequiredService<WorkspaceWatcherLease>().Watcher);
    }
    finally
    {
      Directory.Delete(workspace.FullName, true);
    }
  }

  /// <summary>Disposing ONE of two containers over a workspace leaves the survivor's watcher
  /// running: closing a tab must not blind the tab that is still open.</summary>
  [Fact]
  public async Task DisposingOneOfTwoContainers_LeavesTheSurvivorsWatcherRunning()
  {
    DirectoryInfo workspace = Workspace("a.cs");
    try
    {
      using TestAppDatabase db = TestAppDatabase.Create();
      ServiceProvider closing = Compose(db, workspace.FullName);
      await using ServiceProvider staying = Compose(db, workspace.FullName);

      WorkspaceWatcherLease survivor = staying.GetRequiredService<WorkspaceWatcherLease>();
      survivor.EnsureStarted();
      Assert.Same(survivor.Watcher, closing.GetRequiredService<WorkspaceWatcherLease>().Watcher);

      await closing.DisposeAsync();

      Assert.True(survivor.Watcher.IsStarted);
    }
    finally
    {
      Directory.Delete(workspace.FullName, true);
    }
  }
}
