using System.Text.Json;

namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>C3: the drag path reaches the input layer at the wire level. Fix round 4
///     (double-guard ruling): the drag sink is a recording double, so the coordinate
///     and element wire tests prove the ROUTE (gate, endpoint read, element-center
///     resolve, honest receipt) and dispatch NO real input at desktop coordinates.
///     The row SHAPE the sink builds is pinned with fake metrics in
///     DragMathPinTests / DragSequencePinTests.</summary>
public class DragWireTests
{
  [Fact]
  public void Drag_WithCoordinateTargets_DispatchesThroughTheDragSink_NoRealInput()
  {
    PipeServer server = FakeConnectionFactory.WithAlwaysSucceedingDrag(foreground: 4242);
    _ = server.Dispatch(0, "authenticate", JsonDocument.Parse("{" + q("token") + ":" + q("t") + "}").RootElement, connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "drag",
        JsonDocument.Parse("{" + q("x") + ":10," + q("y") + ":20," + q("to_x") + ":300," + q("to_y") + ":400}").RootElement, connectionId: 1);
    Assert.Null(reply.Error);
    Assert.True(server.InputDispatch.DragCalls == 1, "the drag must reach the drag sink");
    Assert.Equal((10, 20), server.InputDispatch.LastDragFrom);
    Assert.Equal((300, 400), server.InputDispatch.LastDragTo);
  }

  [Fact]
  public void Drag_WithElementTarget_TakesTheCoordinatePath_AtTheElementCenter()
  {
    // C3 end-to-end wire proof: the element target resolves to the element's bounds
    // center through the wired resolver and enters the SAME drag sink at that center
    // as the from-point - the coordinate path - with the same honest accepted receipt.
    PipeServer server = FakeConnectionFactory.WithAlwaysSucceedingDrag(foreground: 4242);
    server.InputDispatch.SetElementOps(new FixedBoundsResolver([(9, 100, 80)]));
    _ = server.Dispatch(0, "authenticate", JsonDocument.Parse("{" + q("token") + ":" + q("t") + "}").RootElement, connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "drag",
        JsonDocument.Parse("{" + q("element") + ":9," + q("to_x") + ":700," + q("to_y") + ":800}").RootElement, connectionId: 1);
    Assert.Null(reply.Error);
    Assert.True(server.InputDispatch.DragCalls == 1, "the element drag must reach the drag sink");
    Assert.Equal((100, 80), server.InputDispatch.LastDragFrom); // the element center
    Assert.Equal((700, 800), server.InputDispatch.LastDragTo);
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
    Assert.Equal(0, server.InputDispatch.DragCalls);
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
    Assert.Equal(0, server.InputDispatch.DragCalls);
  }

  private static string q(string s) => "\"" + s + "\"";

  /// <summary>Element-center fake over the bounds-resolver seam: element 9 centers at
  ///     the given point, everything else is unresolvable.</summary>
  private sealed class FixedBoundsResolver(IReadOnlyDictionary<int, (int X, int Y)> centers) : IElementBoundsResolver
  {
    public FixedBoundsResolver(IEnumerable<(int Index, int X, int Y)> entries)
      : this(entries.ToDictionary(e => e.Index, e => (e.X, e.Y))) { }

    public bool TryResolveBoundsCenter(int element, out int centerX, out int centerY)
    {
      if (centers.TryGetValue(element, out (int X, int Y) center))
      {
        centerX = center.X;
        centerY = center.Y;
        return true;
      }

      centerX = 0;
      centerY = 0;
      return false;
    }
  }
}
