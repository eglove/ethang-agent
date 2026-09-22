namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>C (drag math pins): absolute pointer rows must carry MOVE|ABSOLUTE|VIRTUALDESK
///     (ABSOLUTE|VIRTUALDESK alone is a no-op event), normalize against the virtual-desktop
///     EXTENTS (SM_CX/CYVIRTUALSCREEN 78/79 - the committed code divided by the ORIGIN
///     metrics 76/77, a divide-by-zero on a single monitor at origin 0), clamp to the
///     0..65535 normalized range, guard zero extents as an honest failure, and interpolate
///     the drag path inclusively between both endpoints.</summary>
public class DragMathPinTests
{
  [Fact]
  public void MoveRows_CarryMoveAbsoluteVirtualDeskFlags()
  {
    uint flags = DragFlagsProbe.MoveRowFlags(10, 20);
    Assert.Equal(0x8001u | 0x4000u, flags);
  }

  [Fact]
  public void Coordinates_NormalizeAgainstExtents_NotOrigin()
  {
    int cx = DragFlagsProbe.Metric(78);
    int cy = DragFlagsProbe.Metric(79);
    Assert.True(cx > 0 && cy > 0, "this machine must report positive virtual-desktop extents");
    (uint _, int dx, int dy) = DragFlagsProbe.MoveRowFlagsAndPoint(10, 20);
    Assert.Equal((int)Math.Round(10 * 65535.0 / cx), dx);
    Assert.Equal((int)Math.Round(20 * 65535.0 / cy), dy);
  }

  [Fact]
  public void Coordinates_OutsideTheDesktop_ClampIntoTheNormalizedRange()
  {
    (_, int dx, int dy) = DragFlagsProbe.MoveRowFlagsAndPoint(-5, DragFlagsProbe.Metric(79) + 5);
    Assert.Equal(0, dx);
    Assert.Equal(65535, dy);
  }

  [Fact]
  public void ZeroExtents_RefuseAsHonestFailure_NoDivideByZeroNoNaN()
  {
    // Zero extents (the committed single-monitor divide-by-zero) must fail the send
    // honestly (false - no event accepted), never throw or produce NaN coordinates.
    bool sent = DragFlagsProbe.SendDragWithExtents(0, 0, 10, 20);
    Assert.False(sent);
  }

  [Fact]
  public void Interpolation_CoversThePath_AndReachesTheEndpoint()
  {
    // SendDrag leads with an explicit from-move, then interpolates steps 1..steps; the
    // last interpolated step IS the to-point, so the path is covered end to end.
    (int lastX, int lastY) = DragFlagsProbe.StepPoint(100, 200, 300, 400, steps: 8, step: 8);
    Assert.Equal((300, 400), (lastX, lastY));
    (int midX, int midY) = DragFlagsProbe.StepPoint(100, 200, 300, 400, steps: 8, step: 4);
    Assert.Equal((200, 300), (midX, midY));
  }
}
