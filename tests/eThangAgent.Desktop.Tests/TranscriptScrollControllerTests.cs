using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

/// <summary>Unit tests for the sticky auto-scroll decision logic. Pure C# -
///     no Avalonia, no headless platform: geometry in, decisions out.</summary>
public class TranscriptScrollControllerTests
{
  [Fact]
  public void New_Controller_Is_Stuck_To_Bottom()
  {
    TranscriptScrollController c = new();
    Assert.True(c.StuckToBottom);
  }

  [Fact]
  public void At_Bottom_Reported_As_Stuck()
  {
    TranscriptScrollController c = new();
    c.ObserveScroll(extent: 500, viewport: 200, offset: 300);
    Assert.True(c.StuckToBottom);
  }

  [Fact]
  public void Just_Inside_Tolerance_Is_Stuck()
  {
    TranscriptScrollController c = new();
    c.ObserveScroll(extent: 500, viewport: 200, offset: 298); // 2 from bottom
    Assert.True(c.StuckToBottom);
  }

  [Fact]
  public void Upward_Move_Beyond_Tolerance_Unsticks()
  {
    TranscriptScrollController c = new();
    c.ObserveScroll(extent: 500, viewport: 200, offset: 300);
    c.ObserveScroll(extent: 500, viewport: 200, offset: 295); // 5 up, 5 from bottom
    Assert.False(c.StuckToBottom);
  }

  [Fact]
  public void Drag_From_Top_Past_Middle_Unsticks()
  {
    TranscriptScrollController c = new();
    c.ObserveScroll(extent: 1000, viewport: 200, offset: 0);
    c.ObserveScroll(extent: 1000, viewport: 200, offset: 400); // one drag to the middle
    Assert.False(c.StuckToBottom);
  }

  [Fact]
  public void Zero_Geometry_First_Pass_Is_Ignored()
  {
    TranscriptScrollController c = new();
    c.ObserveScroll(0, 0, 0);
    Assert.True(c.StuckToBottom);
    Assert.True(c.ExtentFits);
    Assert.Equal(0, c.LastOffset);
  }

  [Fact]
  public void Near_Bottom_Within_Tolerance_Is_Stuck()
  {
    TranscriptScrollController c = new();
    c.ObserveScroll(extent: 500, viewport: 200, offset: 297);
    Assert.True(c.StuckToBottom);
  }

  [Fact]
  public void User_Scroll_Up_Unsticks()
  {
    TranscriptScrollController c = new();
    c.ObserveScroll(500, 200, 300);
    c.ObserveScroll(500, 200, 250);
    Assert.False(c.StuckToBottom);
  }

  [Fact]
  public void Scrolling_Back_To_Bottom_Resticks()
  {
    TranscriptScrollController c = new();
    c.ObserveScroll(500, 200, 250);
    c.ObserveScroll(500, 200, 300);
    Assert.True(c.StuckToBottom);
  }

  [Fact]
  public void Should_AutoScroll_True_When_Stuck_And_Entry_Is_Agent_Voice()
  {
    TranscriptScrollController c = new();
    Assert.True(c.ShouldAutoScroll(isUserEntry: false));
  }

  [Fact]
  public void Should_AutoScroll_False_When_Unstuck_Even_For_Agent_Voice()
  {
    TranscriptScrollController c = new();
    c.ObserveScroll(500, 200, 300);
    c.ObserveScroll(500, 200, 100); // the user scrolled up
    Assert.False(c.ShouldAutoScroll(isUserEntry: false));
  }

  [Fact]
  public void Should_AutoScroll_False_For_User_Entries_Even_When_Stuck()
  {
    TranscriptScrollController c = new();
    Assert.False(c.ShouldAutoScroll(isUserEntry: true));
  }

  [Fact]
  public void Programmatic_Jump_To_Bottom_Resticks()
  {
    TranscriptScrollController c = new();
    c.ObserveScroll(500, 200, 100);
    c.RequestScrollToEnd();
    Assert.True(c.StuckToBottom);
    Assert.True(c.ShouldScrollToEnd);
  }

  [Fact]
  public void End_Key_On_Idle_Controller_Requests_Scroll_To_End()
  {
    TranscriptScrollController c = new();
    c.RequestScrollToEnd();
    Assert.True(c.ShouldScrollToEnd);
  }

  [Fact]
  public void Content_Growth_At_Pinned_Tail_Does_Not_Unstick()
  {
    TranscriptScrollController c = new();
    c.ObserveScroll(500, 200, 300); // pinned at tail
    // Content grows under a pinned tail: offset stays 300, extent jumps.
    c.ObserveScroll(700, 200, 300);
    Assert.True(c.StuckToBottom);
  }

  [Fact]
  public void Extent_Smaller_Than_Viewport_Is_Always_Stuck()
  {
    TranscriptScrollController c = new();
    c.ObserveScroll(150, 200, 0);
    Assert.True(c.StuckToBottom);
  }

  [Fact]
  public void LastOffset_Tracks_Observed_Offset_For_Tab_Restore()
  {
    TranscriptScrollController c = new();
    c.ObserveScroll(1000, 200, 123);
    Assert.Equal(123, c.LastOffset);
  }

  [Fact]
  public void Observed_Up_Scroll_Is_Remembered_As_Unstuck()
  {
    TranscriptScrollController c = new();
    c.ObserveScroll(1000, 200, 500);
    c.ObserveScroll(1000, 200, 100);
    Assert.False(c.StuckToBottom);
    Assert.Equal(100, c.LastOffset);
  }
  [Fact]
  public void Tiny_Jitter_At_A_NonBottom_Position_Does_Not_Unstick()
  {
    TranscriptScrollController c = new();
    c.ObserveScroll(extent: 1000, viewport: 200, offset: 400); // user scrolled up: unstuck

    // A reactivation transient (e.g. the unfocused-window refocus jump): a few-DIP
    // offset report at the same reading position is layout jitter, not a scroll.
    c.ObserveScroll(extent: 1000, viewport: 200, offset: 398);

    Assert.False(c.StuckToBottom, "sub-tolerance jitter must not change stickiness");
    Assert.Equal(398, c.LastOffset, 1);
  }

  [Fact]
  public void Small_Transient_Toward_The_Bottom_Does_Not_Restick()
  {
    TranscriptScrollController c = new();
    c.ObserveScroll(extent: 1000, viewport: 200, offset: 400); // unstuck, mid-content

    // A transient few-DIP move toward the bottom must not re-stick the transcript
    // (the refocus guard): only a real move to the bottom tolerance band resticks.
    c.ObserveScroll(extent: 1000, viewport: 200, offset: 403);

    Assert.False(c.StuckToBottom, "small transients must not restick");
  }

  [Fact]
  public void A_Large_Transient_Toward_The_Bottom_Can_Restick_But_Not_MidBand()
  {
    TranscriptScrollController c = new();
    c.ObserveScroll(extent: 1000, viewport: 200, offset: 400); // unstuck

    c.ObserveScroll(extent: 1000, viewport: 200, offset: 500);

    Assert.False(c.StuckToBottom, "a mid-band landing stays unstuck");
    Assert.Equal(500, c.LastOffset, 1);
  }

  [Fact]
  public void SubTolerance_Transient_Into_The_Bottom_Band_Does_Not_Restick()
  {
    TranscriptScrollController c = new();
    c.ObserveScroll(extent: 1000, viewport: 200, offset: 794); // 6 from bottom: unstuck
    Assert.False(c.StuckToBottom);

    // A reactivation transient of a few DIPs landing inside the bottom tolerance
    // band must not re-stick: the user was reading above the tail, and a false
    // re-stick lets the next entry yank the view down (the refocus jump).
    c.ObserveScroll(extent: 1000, viewport: 200, offset: 797); // 3 from bottom

    Assert.False(c.StuckToBottom, "a jitter-sized move must not restick the transcript");
  }
}
