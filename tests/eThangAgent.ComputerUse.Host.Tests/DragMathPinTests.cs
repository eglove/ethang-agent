namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>C (drag math pins), fix round 4: absolute pointer rows must carry
///     MOVE|ABSOLUTE|VIRTUALDESK, normalize screen points against the virtual-desktop
///     EXTENTS passed IN (fake metrics - the controller ruling's injection; the
///     committed bug divided by the ORIGIN metrics 76/77), clamp into the 0..65535
///     normalized range, refuse zero extents, and interpolate the drag path
///     inclusively. Pure math: BuildMoveParts takes the extents as arguments.</summary>
public class DragMathPinTests
{
  [Fact]
  public void MoveParts_CarryMoveAbsoluteVirtualDeskFlags()
  {
    (uint flags, int _, int _) = NativeInput.BuildMoveParts(10, 20, 1920, 1080);
    Assert.Equal(0x8001u | 0x4000u, flags);
  }

  [Fact]
  public void Coordinates_NormalizeAgainstTheInjectedExtents_NotOrigin()
  {
    // 10/1920 and 20/1080 over fake extents: the exact ratio math the committed code
    // got wrong by dividing by SM_X/YVIRTUALSCREEN (the origin).
    (uint _, int dx, int dy) = NativeInput.BuildMoveParts(10, 20, 1920, 1080);
    Assert.Equal((int)Math.Round(10 * 65535.0 / 1920), dx);
    Assert.Equal((int)Math.Round(20 * 65535.0 / 1080), dy);
  }

  [Fact]
  public void Coordinates_OutsideTheDesktop_ClampIntoTheNormalizedRange()
  {
    (uint _, int dx, int dy) = NativeInput.BuildMoveParts(-5, 1130, 1920, 1080);
    Assert.Equal(0, dx);
    Assert.Equal(65535, dy);
  }

  [Fact]
  public void ZeroExtents_RefuseAsHonestFailure_NoDivideByZeroNoNaN()
  {
    // Zero extents (the committed single-monitor divide-by-zero) must produce NO rows
    // - the honest refusal - never a throw and never NaN coordinates.
    Assert.Empty(NativeInput.BuildDragRows("left", 10, 20, 30, 40, 0, 0));
    (uint flags, int dx, int dy) = NativeInput.BuildMoveParts(10, 20, 0, 0);
    Assert.Equal(0u, flags);
    Assert.Equal(0, dx);
    Assert.Equal(0, dy);
  }

  [Fact]
  public void Interpolation_CoversThePath_AndReachesTheEndpoint()
  {
    (int lastX, int lastY) = NativeInput.StepPoint(100, 200, 300, 400, steps: 8, step: 8);
    Assert.Equal((300, 400), (lastX, lastY));
    (int midX, int midY) = NativeInput.StepPoint(100, 200, 300, 400, steps: 8, step: 4);
    Assert.Equal((200, 300), (midX, midY));
  }
}
