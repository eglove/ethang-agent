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
      await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken).ConfigureAwait(true);

      // Fresh app re-attaches on the SAME pipe: the host is still serving.
      NamedPipeChildTransport second = await NamedPipeChildTransport.ConnectToHostAsync(pipeName,
          TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
      RemoteAgentRuntime secondRuntime = new(second);
      using CancellationTokenSource secondLoop = new();
      _ = secondRuntime.RunReceiveLoopAsync(secondLoop.Token);

      // Wait for the host's declare envelope naming the still-running child (R3.1).
      for (int i = 0; i < 100 && !secondRuntime.DeclaredLiveChildren.Contains(child.Id.Value); i++)
      {
        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken).ConfigureAwait(true);
      }

      Assert.Contains(child.Id.Value, secondRuntime.DeclaredLiveChildren);

      // The child settles INTO THE NEW CONNECTION (the host's settle emission targets
      // the current transport). A failed-fast run settles Completed/Failed quickly;
      // either way the outcome is retrievable through the re-attached runtime.
      Result<AgentRunOutcome> outcome = await secondRuntime.WhenSettledAsync(child.Id,
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
