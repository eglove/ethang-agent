namespace eThangAgent.Desktop.ViewModels;

/// <summary>Sticky auto-scroll decision logic for one agent transcript, extracted
///     as pure C# so the sticky rules are unit-testable without a UI. The view
///     feeds it scroll geometry per ScrollChanged and entry kinds per append; it
///     answers whether the view should scroll. Holds no Avalonia types.</summary>
internal sealed class TranscriptScrollController
{
  // Within this distance (DIPs) of the bottom edge the view still counts as
  // stuck - ScrollChanged reports transient geometry mid-layout.
  private const double BottomTolerance = 4.0;

  // A move within this band of the last offset is layout jitter or a reactivation
  // transient (an unfocused window re-laid-out on refocus can report a few DIPs of
  // drift), not a deliberate scroll. Ignored for stickiness in either direction -
  // but LastOffset still tracks it, so the same transient is not re-detected
  // forever. Beyond the band it is a real user scroll and stickiness re-evaluates.
  // Small enough to be invisible as a scroll, larger than encoder rounding.
  private const double TransientBand = 3.0;

  /// <summary>Whether the transcript is pinned to the bottom (the auto-scroll
  ///     precondition). Starts true: a fresh transcript hugs the bottom.</summary>
  public bool StuckToBottom { get; private set; } = true;

  /// <summary>True while the content fits without scrolling (the bottom is on
  ///     screen, so nothing can unstick).</summary>
  public bool ExtentFits { get; private set; } = true;

  /// <summary>Last offset seen by <see cref="ObserveScroll"/> - what a rebuilt
  ///     view re-applies when the transcript was left unstuck (tab switch).</summary>
  public double LastOffset { get; private set; }

  /// <summary>Feed every ScrollChanged here once real geometry exists. Content
  ///     growth under a pinned tail keeps the offset constant, so only a real
  ///     offset move can change stickiness: landing near the bottom sticks,
  ///     landing anywhere else unsticks. Content smaller than the viewport is
  ///     always stuck (the bottom is on screen).</summary>
  public void ObserveScroll(double extent, double viewport, double offset)
  {
    if (extent <= 0 || viewport <= 0)
    {
      return; // no real geometry yet (first layout passes)
    }

    if (extent <= viewport)
    {
      ExtentFits = true;
      StuckToBottom = true;
      LastOffset = offset;
      return;
    }

    ExtentFits = false;
    double move = Math.Abs(offset - LastOffset);
    if (move > TransientBand)
    {
      StuckToBottom = extent - viewport - offset <= BottomTolerance;
    }

    LastOffset = offset;
  }

  /// <summary>Answers whether the view should scroll for a transcript entry being
  ///     appended: agent-voice entries scroll only while stuck; user entries never
  ///     scroll (the user knows what they just typed).</summary>
  public bool ShouldAutoScroll(bool isUserEntry) => !isUserEntry && StuckToBottom;

  /// <summary>End key / programmatic jump: re-stick and raise the one-shot
  ///     ScrollToEnd request.</summary>
  public void RequestScrollToEnd()
  {
    StuckToBottom = true;
    ShouldScrollToEnd = true;
  }

  /// <summary>The one-shot ScrollToEnd request flag.</summary>
  public bool ShouldScrollToEnd { get; private set; }

  /// <summary>Clears the one-shot ScrollToEnd request once the view performed it.</summary>
  public void ClearScrollToEnd() => ShouldScrollToEnd = false;
}
