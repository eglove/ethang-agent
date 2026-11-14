using System.Diagnostics;
using eThangAgent.Composition;
using eThangAgent.TestSupport;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>THE ACK LOOP, MEASURED (2026-11-14): both pumps acked every envelope they
///     received, including acks, so each side's ack was acked back forever. The app and the
///     child host each burned ~6 cores and ~50 MB/s of allocation while doing no work.
///     <para><see cref="AckLoopTests"/> pins the RULE; this pins the CONSEQUENCE end to end,
///     because a rule test cannot see a pump that stops asking. A connected, idle host must
///     cost almost nothing: the loop showed up as sustained CPU on both sides, so the
///     assertion is on the host's own processor time across a real connection.</para>
///     <para>The bound is deliberately loose - this is a DETECTOR for a runaway loop (which
///     pinned multiple cores solid), not a latency contract. An idle host that stays under a
///     fraction of one core cannot be running the loop.</para></summary>
[Trait("Category", "Process")]
public class AckLoopCpuTests
{
  private static string FindHostExe() =>
      ChildHostExeLocator.Resolve(
          ChildHostExeLocator.FindRepoRoot(typeof(AckLoopCpuTests).Assembly.Location),
          AppContext.BaseDirectory);

  /// <summary>An idle connected host must not burn CPU. Before the fix this measured
  ///     several cores; the loop is gone when the host is essentially asleep.</summary>
  [Fact]
  public async Task IdleConnectedHost_DoesNotBurnCpu()
  {
    string hostExe = FindHostExe();
    Assert.True(File.Exists(hostExe), "child host exe not built");

    string pipeName = "ethang-cpu-" + Guid.NewGuid().ToString("N");
    string scratch = Path.Combine(TestTempRoot.Root, "cpu-" + Guid.NewGuid().ToString("N"));
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
    NamedPipeChildTransport? transport = null;
    CancellationTokenSource? loopCts = null;
    try
    {
      transport = await NamedPipeChildTransport
          .ConnectToHostAsync(pipeName, TestContext.Current.CancellationToken)
          .WaitAsync(TestCeiling.Value, TestContext.Current.CancellationToken)
          .ConfigureAwait(true);

      // The app-side pump, exactly as composition runs it: this is the side that acked acks.
      RemoteAgentRuntime runtime = new(transport);
      loopCts = new CancellationTokenSource();
      _ = runtime.RunReceiveLoopAsync(loopCts.Token);

      // Let the connection settle, then measure the host's own processor time.
      await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken).ConfigureAwait(true);

      host.Refresh();
      TimeSpan cpuBefore = host.TotalProcessorTime;
      DateTime wallBefore = DateTime.UtcNow;

      await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);

      host.Refresh();
      TimeSpan cpuUsed = host.TotalProcessorTime - cpuBefore;
      double wallSeconds = (DateTime.UtcNow - wallBefore).TotalSeconds;
      double cores = cpuUsed.TotalSeconds / wallSeconds;

      // The loop pinned multiple cores; an idle host uses a small fraction of one.
      Assert.True(cores < 0.5,
          $"idle connected host burned {cores:F2} cores over {wallSeconds:F1}s - the ack loop is back");
    }
    finally
    {
      if (loopCts is not null)
      {
        await loopCts.CancelAsync().ConfigureAwait(true);
      }
      loopCts?.Dispose();
      if (transport is not null)
      {
        await transport.DisposeAsync().ConfigureAwait(true);
      }

      if (!host.HasExited)
      {
        host.Kill(entireProcessTree: true);
      }
    }
  }
}
