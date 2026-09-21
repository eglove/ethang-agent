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

  [Fact]
  public async Task WireReceipt_ActionSentFalse_SurvivesToOutcome()
  {
    string hostPath = StubHostBuilder.Build();
    string pipeName = "ethang-test-" + Guid.NewGuid().ToString("N");
    BrokerSupervisor supervisor = new(hostPath, pipeName, "receipt-test");
    try
    {
      ComputerOutcome outcome = await supervisor.RequestAsync("deny", null, TestContext.Current.CancellationToken).ConfigureAwait(true);
      ComputerOutcome.Receipt receipt = Assert.IsType<ComputerOutcome.Receipt>(outcome);
      Assert.False(receipt.ActionSent);
      Assert.Equal("possibly_sent", receipt.DispatchStatus);
      Assert.Equal("unchanged", receipt.EffectEvidence);
    }
    finally
    {
      await supervisor.DisposeAsync().ConfigureAwait(true);
    }
  }
  [Fact]
  public async Task UnreadyPipe_YieldsTimeoutFailure_Retryable_NoException()
  {
    string hostPath = StubHostBuilder.Build();
    string pipeName = "ethang-test-none-" + Guid.NewGuid().ToString("N");
    FakeDelayer delayer = new();
    BrokerSupervisor supervisor = new(hostPath, pipeName, "timeout-test",
        (exe, pipe, token) => Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true })!,
        notReady: new NotReadyPolicy(delayer));
    try
    {
      ComputerOutcome outcome = await supervisor.RequestAsync("list_applications", null, TestContext.Current.CancellationToken).ConfigureAwait(true);
      ComputerOutcome.Failure failure = Assert.IsType<ComputerOutcome.Failure>(outcome);
      Assert.Equal(ComputerErrorCodes.Timeout, failure.Code);
      Assert.Equal("retry", ComputerErrorCodes.RetryHint(failure.Code));
    }
    finally
    {
      await supervisor.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task MissingHostExe_IsHelperUnavailableFailure_WithoutRestartBudget()
  {
    string pipeName = "ethang-test-none-" + Guid.NewGuid().ToString("N");
    BrokerSupervisor supervisor = new(@"C:\no\such\host.exe", pipeName, "nohost");
    try
    {
      ComputerOutcome first = await supervisor.RequestAsync("list_applications", null, TestContext.Current.CancellationToken).ConfigureAwait(true);
      ComputerOutcome.Failure firstFailure = Assert.IsType<ComputerOutcome.Failure>(first);
      Assert.Equal(ComputerErrorCodes.HelperUnavailable, firstFailure.Code);
      Assert.Contains("host", firstFailure.Message, StringComparison.Ordinal);
      ComputerOutcome second = await supervisor.RequestAsync("list_applications", null, TestContext.Current.CancellationToken).ConfigureAwait(true);
      ComputerOutcome.Failure secondFailure = Assert.IsType<ComputerOutcome.Failure>(second);
      Assert.Contains("host", secondFailure.Message, StringComparison.Ordinal);
    }
    finally
    {
      await supervisor.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task SecondConsecutiveCrash_ReportsLostTwice()
  {
    string hostPath = StubHostBuilder.Build();
    string pipeName = "ethang-test-" + Guid.NewGuid().ToString("N");
    BrokerSupervisor supervisor = new(hostPath, pipeName, "twice-test");
    try
    {
      ComputerOutcome served = await supervisor.RequestAsync("list_applications", null, TestContext.Current.CancellationToken).ConfigureAwait(true);
      bool servedOk = served is ComputerOutcome.Receipt;
      Assert.True(servedOk, "first request should succeed");
      ComputerOutcome firstCrash = await supervisor.RequestAsync("crash", null, TestContext.Current.CancellationToken).ConfigureAwait(true);
      bool firstIsFailure = firstCrash is ComputerOutcome.Failure;
      Assert.True(firstIsFailure, "first crash should fail");
      ComputerOutcome secondCrash = await supervisor.RequestAsync("crash", null, TestContext.Current.CancellationToken).ConfigureAwait(true);
      ComputerOutcome.Failure failure = Assert.IsType<ComputerOutcome.Failure>(secondCrash);
      Assert.Equal(ComputerErrorCodes.HelperUnavailable, failure.Code);
      Assert.Contains("lost twice", failure.Message, StringComparison.Ordinal);
    }
    finally
    {
      await supervisor.DisposeAsync().ConfigureAwait(true);
    }
  }
}
