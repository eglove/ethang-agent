using System.Diagnostics;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>Wire-level E2E against a REAL ChildHost process: connect over the named pipe,
///     interrupt round-trips, and a declared failure when the host dies. The host exe is
///     built by the solution (project reference); every await is deadline-bounded per
///     deadlock vigilance.</summary>
public class RemoteAgentRuntimeE2ETests
{
  private static string FindHostExe()
  {
    DirectoryInfo? dir = new(typeof(RemoteAgentRuntimeE2ETests).Assembly.Location);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "eThangAgent.slnx")))
    {
      dir = dir.Parent;
    }

    string repo = dir!.FullName;
    return Path.Combine(repo, "src", "eThangAgent.ChildHost", "bin", "Debug", "net10.0", "eThangAgent.ChildHost.exe");
  }

  [Fact]
  public async Task HostProcess_Connected_InterruptsRoundTrip_AndDeclaredFailureOnDeath()
  {
    string hostExe = FindHostExe();
    Assert.True(File.Exists(hostExe), "child host exe not built");

    string pipeName = "ethang-e2e-" + Guid.NewGuid().ToString("N");
    string scratch = Path.Combine(Path.GetTempPath(), "e2e-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(scratch);
    await File.WriteAllTextAsync(Path.Combine(scratch, "settings.json"), "{}",
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
      Task<NamedPipeChildTransport> connected = NamedPipeChildTransport.ConnectToHostAsync(pipeName,
          TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10),
          TestContext.Current.CancellationToken);
      NamedPipeChildTransport transport = await connected.ConfigureAwait(true);

      RemoteAgentRuntime runtime = new(transport);
      using CancellationTokenSource loopCts = new();
      _ = runtime.RunReceiveLoopAsync(loopCts.Token);

      // Interrupt envelope round-trips without error (no matching run: host no-ops).
      runtime.Interrupt(new AgentId(Guid.NewGuid()));

      // Unknown-id waits fail NotFound immediately (contract) — pinned before teardown.
      Task<Result<AgentRunOutcome>> wait = runtime.WhenSettledAsync(new AgentId(Guid.NewGuid()),
          TestContext.Current.CancellationToken);
      Result<AgentRunOutcome> unknown = await wait.ConfigureAwait(true);
      Assert.False(unknown.IsSuccess);
      Assert.Equal("NotFound", unknown.Error.Code);

      // Kill the host: the pump must fault without hanging; a follow-up receive surfaces
      // the declared closed condition (FR-X3) instead of blocking forever.
      host.Kill(entireProcessTree: true);
      Task receive = transport.ReceiveAsync(TestContext.Current.CancellationToken);
      Task winner = await Task.WhenAny(receive, Task.Delay(TimeSpan.FromSeconds(10),
          TestContext.Current.CancellationToken)).ConfigureAwait(true);
      Assert.True(winner == receive, "receive hung after host death");
    }
    finally
    {
      if (!host.HasExited)
      {
        host.Kill(entireProcessTree: true);
      }
    }
  }
}
