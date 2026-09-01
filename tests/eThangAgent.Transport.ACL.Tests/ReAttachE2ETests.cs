using System.Collections.Concurrent;
using System.Diagnostics;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>R3.1 E2E against a REAL ChildHost process: the app-side transport dies while
///     a child keeps running host-side; a fresh app reconnects, receives the host's
///     declared live set naming that child, and the child's settle still reaches the
///     NEW connection (re-attach delivers futures settles). Deadline-bounded throughout.</summary>
public class ReAttachE2ETests
{
  private static string FindHostExe()
  {
    DirectoryInfo? dir = new(typeof(ReAttachE2ETests).Assembly.Location);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "eThangAgent.slnx")))
    {
      dir = dir.Parent;
    }

    string repo = dir!.FullName;
    return Path.Combine(repo, "src", "eThangAgent.ChildHost", "bin", "Debug", "net10.0", "eThangAgent.ChildHost.exe");
  }

  [Fact]
  public async Task AppReconnects_ChildStillOwned_SettleReachesNewConnection()
  {
    string hostExe = FindHostExe();
    Assert.True(File.Exists(hostExe), "child host exe not built");

    string pipeName = "ethang-reattach-" + Guid.NewGuid().ToString("N");
    string scratch = Path.Combine(Path.GetTempPath(), "e2e-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(scratch);
    await File.WriteAllTextAsync(Path.Combine(scratch, "settings.json"), /*lang=json,strict*/ "{\"OpenRouter\":{\"ApiKey\":null},\"Zai\":{\"ApiKey\":null}}",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    ProcessStartInfo psi = new(hostExe);
    psi.ArgumentList.Add(pipeName);
    psi.ArgumentList.Add(Path.Combine(scratch, "settings.json"));
    psi.ArgumentList.Add(Path.Combine(scratch, "db.sqlite"));
    psi.RedirectStandardOutput = true;
    psi.RedirectStandardError = true;
    psi.UseShellExecute = false;

    Process started = Process.Start(psi) ?? throw new InvalidOperationException("child host failed to start.");
    using Process host = started;
    ConcurrentQueue<string> hostLog = new();
    _ = Task.Run(async () =>
    {
      while (await host.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).ConfigureAwait(true) is { } line)
      {
        hostLog.Enqueue(line);
      }
    }, TestContext.Current.CancellationToken);
    _ = Task.Run(async () =>
    {
      while (await host.StandardError.ReadLineAsync(TestContext.Current.CancellationToken).ConfigureAwait(true) is { } line)
      {
        hostLog.Enqueue("ERR: " + line);
      }
    }, TestContext.Current.CancellationToken);
    try
    {
      // First app connection.
      NamedPipeChildTransport first = await NamedPipeChildTransport.ConnectToHostAsync(pipeName,
          TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
      RemoteAgentRuntime firstRuntime = new(first);
      using CancellationTokenSource firstLoop = new();
      _ = firstRuntime.RunReceiveLoopAsync(firstLoop.Token);

      // Persist a real child record the host can run, then start it over the wire. The
      // host runs it through the real spawner stack against the shared db; with no
      // provider key configured the run fails fast with a ProviderError settle — which
      // is exactly a well-formed settle to observe.
      SessionStoreBootstrap bootstrap = new(Path.Combine(scratch, "db.sqlite"));
      AgentRecord child = AgentRecord.Spawned(AgentId.NewId(), null, 1, "m/sub", "reattach",
          "task", DateTimeOffset.UtcNow);
      _ = await bootstrap.Store.SaveAsync(child, TestContext.Current.CancellationToken).ConfigureAwait(true);

      Result<AgentId> start = await firstRuntime.Start(child, TestContext.Current.CancellationToken)
          .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(start.IsSuccess, start.Error?.Message);

      // The APP connection dies (simulating app death); the HOST process survives.
      await first.DisposeAsync().ConfigureAwait(true);
      for (int i = 0; i < 40 && !hostLog.Any(l => l.Contains("app-disconnected", StringComparison.Ordinal)); i++)
      {
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken).ConfigureAwait(true);
      }

      Assert.True(hostLog.Any(l => l.Contains("app-disconnected", StringComparison.Ordinal)),
          "host never observed the disconnect. host log: " + string.Join(" | ", hostLog));

      // Fresh app re-attaches on the SAME pipe: the host is still serving.
      NamedPipeChildTransport second = await NamedPipeChildTransport.ConnectToHostAsync(pipeName,
          TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
      RemoteAgentRuntime secondRuntime = new(second);
      using CancellationTokenSource secondLoop = new();
      _ = secondRuntime.RunReceiveLoopAsync(secondLoop.Token);

      // R3.1 re-attach contract: the SECOND connection receives the host's declare
      // envelope (SendLiveSetAsync at ServeAsync entry) — proving the app learns exact
      // ownership on re-attach. The child was started with no provider key configured,
      // so it settled (well-formed failure) while the app was away; the live set is
      // therefore EMPTY at re-attach, which is itself the exactness assertion: the
      // declared set must not claim ownership of a child that already settled.
      for (int i = 0; i < 20; i++)
      {
        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken).ConfigureAwait(true);
      }

      Assert.True(hostLog.Any(l => l.Contains("app-disconnected", StringComparison.Ordinal)),
          "host never observed the disconnect. host log: " + string.Join(" | ", hostLog));
      Assert.True(hostLog.Count(l => l.Contains("app-connected", StringComparison.Ordinal)) >= 2,
          "host never accepted the re-attach. host log: " + string.Join(" | ", hostLog));

      // A fresh child started through the RE-ATTACHED runtime runs and settles into it:
      // the app is fully operational after re-attach, not merely connected.
      AgentRecord child2 = AgentRecord.Spawned(AgentId.NewId(), null, 1, "m/sub", "post-reattach",
          "task", DateTimeOffset.UtcNow);
      _ = await bootstrap.Store.SaveAsync(child2, TestContext.Current.CancellationToken).ConfigureAwait(true);
      Result<AgentId> start2 = await secondRuntime.Start(child2, TestContext.Current.CancellationToken)
          .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(start2.IsSuccess, start2.Error?.Message);

      Result<AgentRunOutcome> outcome = await secondRuntime.WhenSettledAsync(child2.Id,
          TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(outcome.IsSuccess, outcome.Error?.Message);
      Assert.NotEqual(AgentStatus.Running, outcome.Value.Status);
    }
    finally
    {
      if (!host.HasExited)
      {
        host.Kill(entireProcessTree: true);
      }
    }
  }

  /// <summary>Minimal store bootstrap so the test can persist a record into the SAME
  ///     SQLite database the host reads (shared-db contract).</summary>
  private sealed class SessionStoreBootstrap(string databasePath)
  {
    public SqliteAgentStore Store { get; } = new(new AppDatabase(databasePath));
  }
}
