using eThangAgent.ToolDomain;
using eThangAgent.Vision.ACL;

namespace eThangAgent.ComputerUse.ACL.Tests;

/// <summary>A broker restart must not leave the controller lease unacquirable: the lease
///     lives on the broker, while the client's belief that it holds one lives on the client
///     and outlives the connection it was granted on.</summary>
[Trait("Category", "Process")]
public class BrokerRestartLeaseTests
{
  private static string Describe(ComputerOutcome outcome) =>
    outcome is ComputerOutcome.Failure failure ? failure.Code + ": " + failure.Message : outcome.ToString() ?? "null";

  [Fact]
  public async Task BrokerKilledMidSession_NextTypeSucceeds()
  {
    string hostPath = StubHostBuilder.Build();
    string pipeName = "ethang-restart-" + Guid.NewGuid().ToString("N");
    BrokerSupervisor supervisor = new(hostPath, pipeName, "restart-ws");
    try
    {
      BrokerComputerAccess access = new(supervisor, new VisionAccess());
      await using (access.ConfigureAwait(true))
      {
        // The session is healthy and holds the lease.
        ComputerOutcome first = await access.ExecuteAsync(
            new ComputerCommand.TypeText("hello", Target: null, App: ComputerAppRef.ByPid(1)),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = Assert.IsType<ComputerOutcome.Receipt>(first);

        // The broker dies under the session, exactly as it did at 22:14:08.
        await supervisor.KillBrokerProcessForTests().ConfigureAwait(true);

        // The replacement broker has no owner. The next type must still land.
        ComputerOutcome after = await access.ExecuteAsync(
            new ComputerCommand.TypeText("world", Target: null, App: ComputerAppRef.ByPid(1)),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.True(after is ComputerOutcome.Receipt { ActionSent: true },
            "the replacement broker must accept input, got: " + Describe(after));
      }
    }
    finally
    {
      await supervisor.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task BrokerKilledMidSession_StopThenTypeSucceeds()
  {
    string hostPath = StubHostBuilder.Build();
    string pipeName = "ethang-restart-stop-" + Guid.NewGuid().ToString("N");
    BrokerSupervisor supervisor = new(hostPath, pipeName, "restart-stop-ws");
    try
    {
      BrokerComputerAccess access = new(supervisor, new VisionAccess());
      await using (access.ConfigureAwait(true))
      {
        _ = await access.ExecuteAsync(
            new ComputerCommand.TypeText("before", Target: null, App: ComputerAppRef.ByPid(1)),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        await supervisor.KillBrokerProcessForTests().ConfigureAwait(true);

        // Stop on the replacement broker reports the lease is FREE, and input must agree.
        ComputerOutcome stopped = await access.ExecuteAsync(
            new ComputerCommand.Stop(Reason: "restart"), TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = Assert.IsType<ComputerOutcome.Receipt>(stopped);

        ComputerOutcome after = await access.ExecuteAsync(
            new ComputerCommand.TypeText("after", Target: null, App: ComputerAppRef.ByPid(1)),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.True(after is ComputerOutcome.Receipt { ActionSent: true },
            "stop reported a free lease, so input must proceed, got: " + Describe(after));
      }
    }
    finally
    {
      await supervisor.DisposeAsync().ConfigureAwait(true);
    }
  }
}
