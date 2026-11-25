namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>Absolute injection is normalized against the VIRTUAL SCREEN, whose origin is
///     not (0, 0) whenever a monitor sits above or left of the primary one. Dividing a
///     screen point by the extent alone drops that origin, so every injected point lands
///     offset by it - the failure that made coordinate clicks on a second display miss
///     their target while element clicks kept working. These pins hold the rebasing.</summary>
public class VirtualDesktopOriginTests
{
  // A 3840x1080 desktop whose left-hand monitor sits at x = -1920.
  private static readonly NativeInput.DesktopMetrics TwoMonitors = new(Cx: 3840, Cy: 1080, Ox: -1920, Oy: 0);

  [Fact]
  public void MoveParts_OnTheLeftMonitor_RebasesThePointOntoTheVirtualDesktop()
  {
    // Screen point (-960, 540) is the centre of the left-hand monitor: one quarter
    // across the virtual desktop and half way down.
    (uint flags, int dx, int dy) = NativeInput.BuildMoveParts(-960, 540, TwoMonitors);

    Assert.Equal(0x0001u | 0x8000u | 0x4000u, flags); // MOVE | ABSOLUTE | VIRTUALDESK
    Assert.Equal(16384, dx); // round(960 * 65535 / 3840)
    Assert.Equal(32768, dy); // round(540 * 65535 / 1080)
  }

  [Fact]
  public void MoveParts_OnThePrimaryMonitor_IsUnchangedByAZeroOrigin()
  {
    NativeInput.DesktopMetrics single = new(Cx: 1920, Cy: 1080);

    (uint _, int dx, int dy) = NativeInput.BuildMoveParts(960, 540, single);

    Assert.Equal(32768, dx);
    Assert.Equal(32768, dy);
  }

  [Fact]
  public void MoveParts_APointLeftOfTheDesktop_ClampsToTheEdge_NeverWraps()
  {
    (uint _, int dx, int dy) = NativeInput.BuildMoveParts(-5000, 540, TwoMonitors);

    Assert.Equal(0, dx); // clamped to the left edge, not wrapped around to the right
    Assert.Equal(32768, dy);
  }

  [Fact]
  public void MoveParts_DegenerateExtent_IsTheRefusalMarker()
  {
    (uint flags, int dx, int dy) = NativeInput.BuildMoveParts(10, 10, new NativeInput.DesktopMetrics(0, 0));

    Assert.Equal(0u, flags);
    Assert.Equal((0, 0), (dx, dy));
  }

  [Fact]
  public void ClickRows_OnTheLeftMonitor_PositionTheMoveRowAtTheRebasedPoint()
  {
    NativeInput.TaggedMouseRow[] rows = NativeInput.BuildClickRows("left", -960, 540, TwoMonitors);

    Assert.Equal("move", rows[0].Role);
    Assert.Equal(16384, rows[0].Dx);
    Assert.Equal(32768, rows[0].Dy);
  }

  [Fact]
  public void DragRows_OnTheLeftMonitor_RebaseBothEndpoints()
  {
    NativeInput.DragRow[] rows = NativeInput.BuildDragRows("left", -1920, 0, 0, 1080, TwoMonitors);

    Assert.Equal(0, rows[0].Dx); // the virtual desktop's left edge
    Assert.Equal(0, rows[0].Dy);
    Assert.Equal(32768, rows[^2].Dx); // the primary monitor's left edge
    Assert.Equal(65535, rows[^2].Dy);
  }
}
