using eThangAgent.ToolDomain;

namespace eThangAgent.ComputerUse.ACL.Tests;

/// <summary>Fix round 5 (F5b): the owner's stop_computer_control reply is a REAL
///     receipt, and BrokerComputerAccess.StopAsync clears its lease flag on it - so a
///     following action re-takes the lease lazily instead of skipping the takeover.
///     The stub host answers every request with an accepted receipt, so each wire
///     round trip is observable through the supervisor's request path.</summary>
public class StopReceiptLeaseTests
{
  [Fact]
  public async Task Stop_OnReceipt_ClearsLeaseFlag_SubsequentActionRetakesLease()
  {
    string hostPath = StubHostBuilder.Build();
    string pipeName = "ethang-stop-" + Guid.NewGuid().ToString("N");
    BrokerSupervisor supervisor = new(hostPath, pipeName, "stop-ws");
    try
    {
      BrokerComputerAccess access = new(supervisor);
      await using (access.ConfigureAwait(true))
      {
        // Action 1 lazily takes the lease (takeover + key), then Stop releases it.
        ComputerOutcome first = await access.ExecuteAsync(
            new ComputerCommand.Key("a", Repeat: null, HoldSeconds: null, ComputerAppRef.ByPid(1)),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = Assert.IsType<ComputerOutcome.Receipt>(first);
        ComputerOutcome stopped = await access.ExecuteAsync(
            new ComputerCommand.Stop(Reason: "done"), TestContext.Current.CancellationToken).ConfigureAwait(true);
        ComputerOutcome.Receipt stopReceipt = Assert.IsType<ComputerOutcome.Receipt>(stopped);
        Assert.True(stopReceipt.ActionSent, "the owner's stop receipt must carry action_sent=true");

        // The flag was cleared: this action performs a FRESH takeover on the wire.
        ComputerOutcome after = await access.ExecuteAsync(
            new ComputerCommand.Key("b", Repeat: null, HoldSeconds: null, ComputerAppRef.ByPid(1)),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = Assert.IsType<ComputerOutcome.Receipt>(after);
      }
    }
    finally
    {
      await supervisor.DisposeAsync().ConfigureAwait(true);
    }
  }
}
