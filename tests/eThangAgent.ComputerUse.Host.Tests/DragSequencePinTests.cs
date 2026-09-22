namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>Fix round 4 (C3 row-shape pins, fake metrics - the controller ruling's
///     injection): the rows production's BuildDragRows composes MUST be, in order:
///     a leading from-move, button down at that position, interpolated
///     MOVE|ABSOLUTE|VIRTUALDESK moves whose LAST row IS the to-point, button up.
///     BuildDragRows takes the extents as
///     arguments, so these pins are pure math - no real metrics, no real input.</summary>
public class DragSequencePinTests
{
  [Fact]
  public void Rows_Run_Down_LeadingFromMove_MovesEndingAtTo_Up()
  {
    NativeInput.DragRow[] rows = NativeInput.BuildDragRows("left", 100, 200, 300, 400, 1920, 1080);
    Assert.True(rows.Length == 11, "down + from-move + 8 moves + up; got " + rows.Length);
    const uint moveFlags = 0x0001u | 0x8000u | 0x4000u;    // MOVE | ABSOLUTE | VIRTUALDESK
    // Win32 drag idiom: the leading move positions the cursor BEFORE the press (a
    // button row acts at the current cursor position). Drag rows carry NORMALIZED
    // coordinates, recomputed here independently over the same fake extents.
    Assert.Equal(0u, rows[0].Type);
    Assert.Equal(moveFlags, rows[0].Flags);
    (uint _, int fromDx, int fromDy) = NativeInput.BuildMoveParts(100, 200, 1920, 1080);
    Assert.Equal(fromDx, rows[0].Dx);                      // the first move IS the from-point
    Assert.Equal(fromDy, rows[0].Dy);
    Assert.Equal(0x0002u, rows[1].Flags);                  // button down AT that position
    Assert.Equal(0x0004u, rows[^1].Flags);                 // button up last
    (int endX, int endY) = NativeInput.StepPoint(100, 200, 300, 400, 8, 8);
    (uint _, int toDx, int toDy) = NativeInput.BuildMoveParts(endX, endY, 1920, 1080);
    Assert.Equal(toDx, rows[^2].Dx);                       // the last move IS the to-point
    Assert.Equal(toDy, rows[^2].Dy);
    for (int i = 2; i < rows.Length - 1; i++)
    {
      Assert.Equal(moveFlags, rows[i].Flags);              // every interpolated row moves
    }
  }

  [Fact]
  public void RightButton_Drag_CarriesRightDown_AndRightUp()
  {
    NativeInput.DragRow[] rows = NativeInput.BuildDragRows("right", 0, 0, 10, 10, 1920, 1080);
    Assert.Equal(0x0008u, rows[1].Flags);
    Assert.Equal(0x0010u, rows[^1].Flags);
  }

  [Fact]
  public void MiddleButton_Drag_CarriesMiddleDown_AndMiddleUp()
  {
    NativeInput.DragRow[] rows = NativeInput.BuildDragRows("middle", 0, 0, 10, 10, 1920, 1080);
    Assert.Equal(0x0020u, rows[1].Flags);
    Assert.Equal(0x0040u, rows[^1].Flags);
  }

  [Fact]
  public void ZeroExtents_YieldNoRows_TheProductionRefusal()
  {
    NativeInput.DragRow[] rows = NativeInput.BuildDragRows("left", 10, 20, 30, 40, 0, 0);
    Assert.Empty(rows);
  }

  [Fact]
  public void NegativeExtents_YieldNoRows_Too()
  {
    NativeInput.DragRow[] rows = NativeInput.BuildDragRows("left", 10, 20, 30, 40, -1, 1080);
    Assert.Empty(rows);
  }
}
