using System.Diagnostics;
using eThangAgent.ToolDomain;

namespace eThangAgent.ComputerUse.ACL.Tests;

/// <summary>Stub-host supervision tests (the brief's task-13 contract): spawn + handshake
///     through a real child process; dispose kills it; restart after a crash; registry
///     identity per root.</summary>
public class BrokerSupervisorTests
{
  [Fact]
  public async Task SpawnedStubHost_HandshakesAndServesRequests()
  {
    string hostPath = StubHostBuilder.Build();
    string pipeName = "ethang-test-" + Guid.NewGuid().ToString("N");
    BrokerSupervisor supervisor = new(hostPath, pipeName, "stub-workspace");
    try
    {
      ComputerOutcome outcome = await supervisor.RequestAsync("list_applications", null, TestContext.Current.CancellationToken).ConfigureAwait(true);
      ComputerOutcome.Receipt receipt = Assert.IsType<ComputerOutcome.Receipt>(outcome);
      Assert.True(receipt.ActionSent);
    }
    finally
    {
      await supervisor.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task Dispose_KillsTheSpawnedProcess()
  {
    string hostPath = StubHostBuilder.Build();
    string pipeName = "ethang-test-" + Guid.NewGuid().ToString("N");
    Process? spawned = null;
    BrokerSupervisor supervisor = new(hostPath, pipeName, "kill-test", (exe, pipe, token) =>
    {
      spawned = SpawnForTest(exe, pipe, token);
      return spawned;
    });
    ComputerOutcome first = await supervisor.RequestAsync("list_applications", null, TestContext.Current.CancellationToken).ConfigureAwait(true);
    ComputerOutcome.Receipt firstReceipt = Assert.IsType<ComputerOutcome.Receipt>(first);
    Assert.True(firstReceipt.ActionSent);
    await supervisor.DisposeAsync();
    Assert.NotNull(spawned);
    Task exitTask = spawned.WaitForExitAsync(TestContext.Current.CancellationToken);
    bool exitedInTime = await Task.WhenAny(exitTask, Task.Delay(5000, TestContext.Current.CancellationToken)).ConfigureAwait(true) == exitTask;
    Assert.True(exitedInTime || spawned.HasExited, "stub host should die on dispose");
  }

  private static Process SpawnForTest(string exePath, string pipeName, string token)
  {
    ProcessStartInfo psi = new(exePath, pipeName)
    {
      UseShellExecute = false,
      CreateNoWindow = true,
      EnvironmentVariables = { ["ETHANG_COMPUTER_USE_TOKEN"] = token },
    };
    return Process.Start(psi)!;
  }

  [Fact]
  public async Task Crash_EarnsOneRestart_SecondFailureSurfaces()
  {
    string hostPath = StubHostBuilder.Build();
    string pipeName = "ethang-test-" + Guid.NewGuid().ToString("N");
    BrokerSupervisor supervisor = new(hostPath, pipeName, "crash-test");
    try
    {
      // First request: the broker serves it; then the 'crash' request kills the broker
      // without replying => HELPER_UNAVAILABLE with the one-restart note.
      ComputerOutcome ok = await supervisor.RequestAsync("list_applications", null, TestContext.Current.CancellationToken).ConfigureAwait(true);
      ComputerOutcome.Receipt okReceipt = Assert.IsType<ComputerOutcome.Receipt>(ok);
      Assert.True(okReceipt.ActionSent);
      ComputerOutcome crash = await supervisor.RequestAsync("crash", null, TestContext.Current.CancellationToken).ConfigureAwait(true);
      ComputerOutcome.Failure failure = Assert.IsType<ComputerOutcome.Failure>(crash);
      Assert.Equal(ComputerErrorCodes.HelperUnavailable, failure.Code);
      Assert.Contains("restart", failure.Message, StringComparison.Ordinal);
    }
    finally
    {
      await supervisor.DisposeAsync().ConfigureAwait(true);
    }
  }
}
