using System.Text.Json;

namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>Fix round 5 (F1/F3): a click or scroll that names a target must ACT at
///     that target. Coordinate targets dispatch a real MOVE|ABSOLUTE|VIRTUALDESK row
///     (the drag path's move machinery) BEFORE the button/wheel rows; element targets
///     route through the UIA element path (invoke / scroll pattern); horizontal wheel
///     scroll is an honest typed action_unavailable. The send sinks are recording
///     doubles, so the wire tests prove the dispatch targeted the requested point
///     while dispatching NO real input; the row shapes are pinned over injected
///     extents (the same seam DragSequencePinTests uses).</summary>
public class InputPositioningTests
{
  // ---- F1: click row shapes over injected extents ----

  [Fact]
  public void ClickRows_CoordinateTarget_MoveRowPrecedesButtonRows_AtTheNormalizedPoint()
  {
    NativeInput.TaggedMouseRow[] rows = NativeInput.BuildClickRows("left", 100, 50, cx: 1000, cy: 500);
    Assert.True(rows.Length >= 3, "a targeted click is move + down + up");
    Assert.Equal(0x0001u | 0x8000u | 0x4000u, rows[0].Flags); // MOVE | ABSOLUTE | VIRTUALDESK
    Assert.Equal(6554, rows[0].Dx); // round(100 * 65535 / 1000)
    Assert.Equal(6554, rows[0].Dy); // round(50 * 65535 / 500)
    Assert.Equal("down", rows[^2].Role);
    Assert.Equal("up", rows[^1].Role);
  }

  [Fact]
  public void ClickRows_DegenerateExtents_YieldNoRows_NeverADegenerateEvent()
  {
    NativeInput.TaggedMouseRow[] rows = NativeInput.BuildClickRows("left", 10, 10, cx: 0, cy: 0);
    Assert.Empty(rows);
  }

  // ---- F3: wheel row over injected pages ----

  [Theory]
  [InlineData("up", 2, 240u)]          // two pages up: +2 * WHEEL_DELTA
  [InlineData("down", 3, 4294966936u)] // three pages down: -360 as uint32
  public void WheelRow_DirectionAndPages_MapToSignedMouseData(string direction, int pages, uint expected)
  {
    NativeInput.TaggedMouseRow row = NativeInput.BuildWheelRow(direction, pages);
    Assert.Equal(0x0800u, row.Flags);      // MOUSEEVENTF_WHEEL
    Assert.Equal(expected, row.MouseData); // signed pages * WHEEL_DELTA(120)
    Assert.Equal(0, row.Dx);
    Assert.Equal(0, row.Dy);
  }

  [Fact]
  public void WheelDelta_Is120() => Assert.Equal(120, NativeInput.WheelDelta);

  // ---- F1 wire: the recording click sink sees the resolved point ----

  [Fact]
  public void Click_CoordinateTarget_DispatchesMoveThenButton_AtTheRequestedPoint_NoRealInput()
  {
    PipeServer server = FakeConnectionFactory.WithRecordingClick(foreground: 4242);
    _ = server.Dispatch(0, "authenticate", JsonDocument.Parse("{\"token\":\"t\"}").RootElement, connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "click",
        JsonDocument.Parse("{\"x\":100,\"y\":50}").RootElement, connectionId: 1);
    Assert.Null(reply.Error);
    Assert.Equal(1, server.InputDispatch.ClickCalls);
    Assert.Equal((100, 50), server.InputDispatch.LastClickPoint);
    Assert.Equal("left", server.InputDispatch.LastClickButton);
  }

  [Fact]
  public void Click_ElementTarget_RoutesThroughTheElementPath_Invoke()
  {
    PipeServer server = FakeConnectionFactory.WithRecordingClick(foreground: 4242);
    CountingElementOps ops = new();
    server.InputDispatch.SetElementOps(ops);
    _ = server.Dispatch(0, "authenticate", JsonDocument.Parse("{\"token\":\"t\"}").RootElement, connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "click",
        JsonDocument.Parse("{\"element\":7}").RootElement, connectionId: 1);
    Assert.Null(reply.Error);
    Assert.Equal([7], ops.Invoked);
    Assert.Equal(0, server.InputDispatch.ClickCalls); // the element path, never the pointer path
  }

  [Fact]
  public void Click_UnresolvableElementTarget_IsTypedInvalidRequest_NothingDispatched()
  {
    PipeServer server = FakeConnectionFactory.WithRecordingClick(foreground: 4242);
    _ = server.Dispatch(0, "authenticate", JsonDocument.Parse("{\"token\":\"t\"}").RootElement, connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "click",
        JsonDocument.Parse("{\"element\":77}").RootElement, connectionId: 1);
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("invalid_request", reply.Error.Value.Code);
    Assert.Equal(0, server.InputDispatch.ClickCalls);
    Assert.Equal(0, server.InputDispatch.SendInputCalls);
  }

  [Fact]
  public void Click_EventStrategy_MismatchedForeground_RefusesBeforeAnyRow()
  {
    PipeServer server = FakeConnectionFactory.WithRecordingClick(foreground: 1);
    _ = server.Dispatch(0, "authenticate", JsonDocument.Parse("{\"token\":\"t\"}").RootElement, connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "click",
        JsonDocument.Parse("{\"x\":5,\"y\":6,\"strategy\":\"event\",\"app_ref\":{\"pid\":4242}}").RootElement, connectionId: 1);
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("foreground_required", reply.Error.Value.Code);
    Assert.Equal(0, server.InputDispatch.ClickCalls);
    Assert.Equal(0, server.InputDispatch.SendInputCalls);
  }

  // ---- F3 wire: scroll ----

  [Fact]
  public void Scroll_Vertical_CoordinateTarget_PositionsCursorThenWheels_NoRealInput()
  {
    PipeServer server = FakeConnectionFactory.WithRecordingClick(foreground: 4242);
    _ = server.Dispatch(0, "authenticate", JsonDocument.Parse("{\"token\":\"t\"}").RootElement, connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "scroll",
        JsonDocument.Parse("{\"x\":12,\"y\":34,\"scroll_direction\":\"down\",\"scroll_amount\":2}").RootElement, connectionId: 1);
    Assert.Null(reply.Error);
    Assert.Equal(1, server.InputDispatch.ScrollCalls);
    Assert.Equal((12, 34), server.InputDispatch.LastScrollPoint);
    Assert.Equal(("down", 2), server.InputDispatch.LastScrollRequest);
  }

  [Fact]
  public void Scroll_ElementTarget_RoutesThroughTheElementPath_ScrollPattern()
  {
    PipeServer server = FakeConnectionFactory.WithRecordingClick(foreground: 4242);
    CountingElementOps ops = new();
    server.InputDispatch.SetElementOps(ops);
    _ = server.Dispatch(0, "authenticate", JsonDocument.Parse("{\"token\":\"t\"}").RootElement, connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "scroll",
        JsonDocument.Parse("{\"element\":7,\"scroll_direction\":\"down\",\"scroll_amount\":1}").RootElement, connectionId: 1);
    Assert.Null(reply.Error);
    Assert.Equal([(7, "down", 1)], ops.Scrolled);
    Assert.Equal(0, server.InputDispatch.ScrollCalls); // element path, never the wheel path
  }

  [Fact]
  public void Scroll_Horizontal_CoordinateTarget_IsTypedActionUnavailable_NothingDispatched()
  {
    PipeServer server = FakeConnectionFactory.WithRecordingClick(foreground: 4242);
    _ = server.Dispatch(0, "authenticate", JsonDocument.Parse("{\"token\":\"t\"}").RootElement, connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "scroll",
        JsonDocument.Parse("{\"x\":1,\"y\":2,\"scroll_direction\":\"left\",\"scroll_amount\":1}").RootElement, connectionId: 1);
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("action_unavailable", reply.Error.Value.Code);
    Assert.Contains("horizontal", reply.Error.Value.Message, StringComparison.Ordinal);
    Assert.Equal(0, server.InputDispatch.ScrollCalls);
    Assert.Equal(0, server.InputDispatch.SendInputCalls);
  }

  [Fact]
  public void Scroll_UnresolvableTarget_IsTypedInvalidRequest_NothingDispatched()
  {
    PipeServer server = FakeConnectionFactory.WithRecordingClick(foreground: 4242);
    _ = server.Dispatch(0, "authenticate", JsonDocument.Parse("{\"token\":\"t\"}").RootElement, connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "scroll",
        JsonDocument.Parse("{\"scroll_direction\":\"up\",\"scroll_amount\":1}").RootElement, connectionId: 1);
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("invalid_request", reply.Error.Value.Code);
    Assert.Equal(0, server.InputDispatch.ScrollCalls);
  }
}

/// <summary>Element-op double wired through SetElementOps: journals invoke, focus, and
///     scroll calls so the element ROUTE is provable without a live UIA tree.</summary>
internal sealed class CountingElementOps : IElementBoundsResolver, IElementActionSink
{
  public List<int> Invoked { get; } = [];
  public List<(int Element, string Direction, int Pages)> Scrolled { get; } = [];
  public List<int> Focused { get; } = [];

  public bool TryResolveBoundsCenter(int element, out int centerX, out int centerY)
  {
    centerX = 100;
    centerY = 90;
    return true;
  }

  public BrokerResponse Invoke(int element)
  {
    Invoked.Add(element);
    return BrokerResponse.Accepted();
  }

  public BrokerResponse Focus(int element)
  {
    Focused.Add(element);
    return BrokerResponse.Accepted();
  }

  public BrokerResponse Scroll(int element, string direction, int pages)
  {
    Scrolled.Add((element, direction, pages));
    return BrokerResponse.Accepted();
  }
}
