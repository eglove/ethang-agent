using eThangAgent.ComputerUse.ACL;

namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>C6 (spec 4): the broker pipe serves CONCURRENT connections; the single controller
///     lease arbitrates across them. Connection A takes over; rival B's takeover fails
///     controller_busy with owner=A's connection id in the message and details.</summary>
public class ConcurrentControllerWireTests
{
  [Fact]
  public async Task SecondConnection_Takeover_FailsControllerBusy_WithOwnerInMessage()
  {
    string pipeName = "ethang-cu-host-test-" + Guid.NewGuid().ToString("N");
    await using HostHarness harness = HostHarness.StartConcurrent(pipeName);

    // A: connect + explicit controller_takeover.
    NdjsonPipeClient clientA = await NdjsonPipeClient.ConnectAsync(pipeName, harness.Token, 1, "windows", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
    BrokerReply takeoverA = await clientA.RequestAsync("controller_takeover", null, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.Null(takeoverA.Error);

    // B: a SECOND client on the same pipe; its takeover must be refused with the owner id.
    NdjsonPipeClient clientB = await NdjsonPipeClient.ConnectAsync(pipeName, harness.Token, 1, "windows", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
    BrokerReply takeoverB = await clientB.RequestAsync("controller_takeover", null, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.NotNull(takeoverB.Error);
    Assert.Equal("controller_busy", takeoverB.Error.Code);
    Assert.Contains("owner=", takeoverB.Error.Message, StringComparison.Ordinal);
    Assert.NotNull(takeoverB.Error.Details);
    Assert.Contains("owner=", takeoverB.Error.Details, StringComparison.Ordinal);

    // Status from B confirms the owner is a DIFFERENT (A's) connection.
    BrokerReply status = await clientB.RequestAsync("controller_status", null, TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = Assert.NotNull(status.Result);
    Assert.False(status.Result.Value.TryGetProperty("owned", out System.Text.Json.JsonElement ownedEl) && ownedEl.GetBoolean());

    await clientA.DisposeAsync().ConfigureAwait(true);
    await clientB.DisposeAsync().ConfigureAwait(true);
  }
}
