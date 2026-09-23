using System.Text.Json;

namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>W-b: pin the broker-side strategy=event foreground gate for coordinate clicks at the
///     wire level: the gate is visible through the public Dispatch surface (InputDispatch.GateAllows
///     + strategy parsing), so review can see the gate exists BEFORE any SendInput runs.</summary>
public class ForegroundGateWireTests
{
  [Fact]
  public void GateAllows_OnlyWhenForegroundPidMatches()
  {
    InputDispatch gate = InputDispatch.Create(foregroundPid: () => 4242);
    Assert.True(gate.GateAllows("event", 4242));
    Assert.False(gate.GateAllows("event", 9999));
    // Non-event strategies do not consult the foreground gate.
    Assert.True(gate.GateAllows(null, 9999));
    Assert.True(gate.GateAllows("a11y", 9999));
  }

  [Fact]
  public void Click_WithEventStrategy_AndMismatchedForeground_IsRefusedBeforeDispatch()
  {
    PipeServer server = FakeConnectionFactory.WithForeground(expected: 4242, actual: 1);
    _ = server.Dispatch(0, "authenticate", JsonDocument.Parse("{" + q("token") + ":" + q("t") + "}").RootElement, connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "click",
        JsonDocument.Parse("{" + q("strategy") + ":" + q("event") + "," + q("app_ref") + ":{" + q("pid") + ":4242}}").RootElement, connectionId: 1);
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("foreground_required", reply.Error.Value.Code);
    Assert.Equal(0, server.InputDispatch.SendInputCalls); // nothing dispatched - gate is BEFORE send
  }

  private static string q(string s) => "\"" + s + "\"";
}
