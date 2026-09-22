using System.Text.Json;

namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>C3: the drag path reaches the input layer at the wire level - coordinate drags
///     dispatch the pointer sequence (send-count > 0), unresolvable targets fail honestly
///     (invalid_request, send-count 0), and the event gate still applies.</summary>
public class DragWireTests
{
  [Fact]
  public void Drag_WithCoordinateTargets_DispatchesThePointerSequence()
  {
    PipeServer server = FakeConnectionFactory.WithForeground(expected: 4242, actual: 4242);
    _ = server.Dispatch(0, "authenticate", JsonDocument.Parse("{" + q("token") + ":" + q("t") + "}").RootElement, connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "drag",
        JsonDocument.Parse("{" + q("x") + ":10," + q("y") + ":20," + q("to_x") + ":300," + q("to_y") + ":400}").RootElement, connectionId: 1);
    Assert.Null(reply.Error);
    Assert.True(server.InputDispatch.SendInputCalls > 0, "the drag must reach the input layer");
  }

  [Fact]
  public void Drag_WithUnresolvableElementTargets_FailsHonestWithoutDispatch()
  {
    PipeServer server = FakeConnectionFactory.WithForeground(expected: 4242, actual: 4242);
    _ = server.Dispatch(0, "authenticate", JsonDocument.Parse("{" + q("token") + ":" + q("t") + "}").RootElement, connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "drag",
        JsonDocument.Parse("{" + q("element") + ":77," + q("to_element") + ":78}").RootElement, connectionId: 1);
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("invalid_request", reply.Error.Value.Code);
    Assert.Equal(0, server.InputDispatch.SendInputCalls);
  }

  [Fact]
  public void Drag_WithEventStrategy_AndMismatchedForeground_RefusesBeforeDispatch()
  {
    PipeServer server = FakeConnectionFactory.WithForeground(expected: 4242, actual: 1);
    _ = server.Dispatch(0, "authenticate", JsonDocument.Parse("{" + q("token") + ":" + q("t") + "}").RootElement, connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "drag",
        JsonDocument.Parse("{" + q("x") + ":1," + q("y") + ":2," + q("to_x") + ":3," + q("to_y") + ":4," + q("strategy") + ":" + q("event") + "}").RootElement, connectionId: 1);
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("foreground_required", reply.Error.Value.Code);
    Assert.Equal(0, server.InputDispatch.SendInputCalls);
  }

  private static string q(string s) => "\"" + s + "\"";
}
