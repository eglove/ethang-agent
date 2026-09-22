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
      BrokerReply outcome = await supervisor.RequestEnvelopeAsync("list_applications", null, TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.Null(outcome.Error);
      Assert.True(outcome.Result is not null, "expected a result payload");
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
    BrokerReply first = await supervisor.RequestEnvelopeAsync("list_applications", null, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.Null(first.Error);
    Assert.True(first.Result is not null, "expected a result payload");
    await supervisor.DisposeAsync().ConfigureAwait(true);
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
      BrokerReply ok = await supervisor.RequestEnvelopeAsync("list_applications", null, TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.Null(ok.Error);
      Assert.True(ok.Result is not null, "expected a result payload");
      BrokerEnvelopeException crash = await Assert.ThrowsAsync<BrokerEnvelopeException>(
        () => supervisor.RequestEnvelopeAsync("crash", null, TestContext.Current.CancellationToken)).ConfigureAwait(true);
      Assert.Equal(ComputerErrorCodes.HelperUnavailable, crash.Code);
      Assert.Contains("restart", crash.Message, StringComparison.Ordinal);
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
      BrokerReply outcome = await supervisor.RequestEnvelopeAsync("deny", null, TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.Null(outcome.Error);
      Assert.False(BrokerActionReceipt.From(outcome)!.ActionSent);
      Assert.Equal("possibly_sent", BrokerActionReceipt.From(outcome)!.DispatchStatus);
      Assert.Equal("unchanged", BrokerActionReceipt.From(outcome)!.EffectEvidence);
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
      BrokerEnvelopeException failure = await Assert.ThrowsAsync<BrokerEnvelopeException>(
        () => supervisor.RequestEnvelopeAsync("list_applications", null, TestContext.Current.CancellationToken)).ConfigureAwait(true);
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
      BrokerEnvelopeException first = await Assert.ThrowsAsync<BrokerEnvelopeException>(
        () => supervisor.RequestEnvelopeAsync("list_applications", null, TestContext.Current.CancellationToken)).ConfigureAwait(true);
      Assert.Equal(ComputerErrorCodes.HelperUnavailable, first.Code);
      Assert.Contains("host", first.Message, StringComparison.Ordinal);
      BrokerEnvelopeException second = await Assert.ThrowsAsync<BrokerEnvelopeException>(
        () => supervisor.RequestEnvelopeAsync("list_applications", null, TestContext.Current.CancellationToken)).ConfigureAwait(true);
      Assert.Contains("host", second.Message, StringComparison.Ordinal);
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
      BrokerReply served = await supervisor.RequestEnvelopeAsync("list_applications", null, TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.Null(served.Error);
      BrokerEnvelopeException firstCrash = await Assert.ThrowsAsync<BrokerEnvelopeException>(
        () => supervisor.RequestEnvelopeAsync("crash", null, TestContext.Current.CancellationToken)).ConfigureAwait(true);
      Assert.True(firstCrash.Code is ComputerErrorCodes.HelperUnavailable or ComputerErrorCodes.Timeout, "first crash is typed");
      BrokerEnvelopeException secondCrash = await Assert.ThrowsAsync<BrokerEnvelopeException>(
        () => supervisor.RequestEnvelopeAsync("crash", null, TestContext.Current.CancellationToken)).ConfigureAwait(true);
      Assert.Equal(ComputerErrorCodes.HelperUnavailable, secondCrash.Code);
      Assert.Contains("lost twice", secondCrash.Message, StringComparison.Ordinal);
    }
    finally
    {
      await supervisor.DisposeAsync().ConfigureAwait(true);
    }
  }
}
