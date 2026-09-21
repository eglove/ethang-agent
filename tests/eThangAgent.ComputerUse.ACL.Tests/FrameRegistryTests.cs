using eThangAgent.ToolDomain;

namespace eThangAgent.ComputerUse.ACL.Tests;

/// <summary>Frame registry contract (the batch brief, task 15): LRU eviction at
///     capacity 16, the 10-minute TTL on the injectable clock, the frame- id shape,
///     pixel-center-to-rect coordinate math including
///     a non-DPI-1 source rect, 'latest' resolution, and the typed misses.
///     </summary>
public class FrameRegistryTests
{
  private static readonly ScreenshotWindowRect Rect = new(100, 200, 800, 600);

  [Fact]
  public void Add_ReturnsFrameRef_WithTheFramePrefixAndEightHexChars()
  {
    FrameRegistry registry = new();

    ComputerFrameRef frame = registry.Add(640, 480, Rect, actionable: true);

    Assert.Matches("^frame-[0-9a-f]{8}$", frame.FrameId);
    Assert.Equal(640, frame.Width);
    Assert.Equal(480, frame.Height);
  }

  [Fact]
  public void Add_EvictsLeastRecentlyUsed_BeyondCapacitySixteen()
  {
    FrameRegistry registry = new();
    List<ComputerFrameRef> added = [];
    for (int i = 0; i < 17; i++)
    {
      added.Add(registry.Add(10, 10, Rect, actionable: true));
    }

    Assert.Equal(16, registry.Count);

    // The FIRST frame was evicted (LRU); the second-oldest survives.
    FrameCoordinateResult evicted = registry.ResolveForCoordinate(added[0].FrameId, 5, 5);
    FrameCoordinateResult.Miss evictedMiss = Assert.IsType<FrameCoordinateResult.Miss>(evicted);
    _ = Assert.IsType<FrameResolutionMiss.UnknownFrame>(evictedMiss.Reason);
    FrameCoordinateResult hit = registry.ResolveForCoordinate(added[1].FrameId, 5, 5);
    _ = Assert.IsType<FrameCoordinateResult.Hit>(hit);
  }

  [Fact]
  public void Resolve_TouchRefreshesLruOrder_SoAnOldFrameSurvives()
  {
    FrameRegistry registry = new();
    List<ComputerFrameRef> added = [];
    for (int i = 0; i < 16; i++)
    {
      added.Add(registry.Add(10, 10, Rect, actionable: true));
    }

    // Touch the OLDEST so it becomes most-recently used.
    FrameCoordinateResult touched = registry.ResolveForCoordinate(added[0].FrameId, 5, 5);
    _ = Assert.IsType<FrameCoordinateResult.Hit>(touched);
    ComputerFrameRef extra = registry.Add(10, 10, Rect, actionable: true); // 17th add evicts LRU = added[1]
    Assert.NotEqual(added[0].FrameId, extra.FrameId);

    Assert.Equal(16, registry.Count);
    FrameCoordinateResult survivor = registry.ResolveForCoordinate(added[0].FrameId, 5, 5);
    _ = Assert.IsType<FrameCoordinateResult.Hit>(survivor);
    FrameCoordinateResult gone = registry.ResolveForCoordinate(added[1].FrameId, 5, 5);
    _ = Assert.IsType<FrameCoordinateResult.Miss>(gone);
  }

  [Fact]
  public void Resolve_AfterTtl_ExpiresFrames_OnTheInjectableClock()
  {
    DateTimeOffset now = DateTimeOffset.UtcNow;
    FrameRegistry registry = new(() => now);
    ComputerFrameRef frame = registry.Add(100, 100, Rect, actionable: true);

    now += TimeSpan.FromMinutes(9);
    FrameCoordinateResult fresh = registry.ResolveForCoordinate(frame.FrameId, 50, 50);
    _ = Assert.IsType<FrameCoordinateResult.Hit>(fresh);

    now += TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1);
    FrameCoordinateResult expired = registry.ResolveForCoordinate(frame.FrameId, 50, 50);
    FrameCoordinateResult.Miss miss = Assert.IsType<FrameCoordinateResult.Miss>(expired);
    _ = Assert.IsType<FrameResolutionMiss.UnknownFrame>(miss.Reason);
    Assert.Equal(0, registry.Count);
    Assert.Null(registry.Latest());
  }

  [Fact]
  public void Resolve_MapsPixelCenterFraction_OntoTheWindowRect()
  {
    FrameRegistry registry = new();
    // 100x100 raster sampled from a 200x200 rect at (1000, 2000): each pixel
    // covers 2x2 screen points; pixel (89, 49) center = (89.5, 49.5)/100 ->
    // screen (1000 + 1.79*100, 2000 + 0.99*100) = (1179, 2099).
    _ = registry.Add(100, 100, new ScreenshotWindowRect(1000, 2000, 200, 200), actionable: true);

    FrameCoordinateResult hit = registry.ResolveForCoordinate("latest", 89, 49);
    FrameResolution resolution = Assert.IsType<FrameCoordinateResult.Hit>(hit).Resolution;
    Assert.Equal(1179, resolution.X);
    Assert.Equal(2099, resolution.Y);
  }

  [Fact]
  public void Resolve_HandlesNonDpi1Rect_WhereRasterIsSmallerThanTheRect()
  {
    FrameRegistry registry = new();
    // A 150%-DPI window: 300x300 points sampled as a 450x450 raster (1.5x).
    _ = registry.Add(450, 450, new ScreenshotWindowRect(0, 0, 300, 300), actionable: true);

    FrameCoordinateResult hit = registry.ResolveForCoordinate("latest", 0, 0);
    FrameResolution resolution = Assert.IsType<FrameCoordinateResult.Hit>(hit).Resolution;
    Assert.Equal(0, resolution.X);
    Assert.Equal(0, resolution.Y);

    FrameCoordinateResult far = registry.ResolveForCoordinate("latest", 449, 449);
    FrameResolution farRes = Assert.IsType<FrameCoordinateResult.Hit>(far).Resolution;
    Assert.Equal(299, farRes.X);
    Assert.Equal(299, farRes.Y);
  }

  [Fact]
  public void Resolve_LastPixelOfEachAxis_StaysInsideTheRect()
  {
    FrameRegistry registry = new();
    _ = registry.Add(100, 100, new ScreenshotWindowRect(0, 0, 100, 100), actionable: true);

    FrameCoordinateResult hit = registry.ResolveForCoordinate("latest", 99, 99);
    FrameResolution resolution = Assert.IsType<FrameCoordinateResult.Hit>(hit).Resolution;
    Assert.Equal(99, resolution.X);
    Assert.Equal(99, resolution.Y);
  }

  [Fact]
  public void Resolve_UnknownFrameId_IsATypedMiss()
  {
    FrameRegistry registry = new();
    FrameCoordinateResult miss = registry.ResolveForCoordinate("frame-deadbeef", 0, 0);
    FrameCoordinateResult.Miss typed = Assert.IsType<FrameCoordinateResult.Miss>(miss);
    _ = Assert.IsType<FrameResolutionMiss.UnknownFrame>(typed.Reason);
  }

  [Fact]
  public void Resolve_OutOfRangePixel_IsATypedMiss()
  {
    FrameRegistry registry = new();
    ComputerFrameRef frame = registry.Add(100, 100, Rect, actionable: true);

    FrameCoordinateResult miss = registry.ResolveForCoordinate(frame.FrameId, 100, 50);
    FrameCoordinateResult.Miss typed = Assert.IsType<FrameCoordinateResult.Miss>(miss);
    _ = Assert.IsType<FrameResolutionMiss.PixelOutOfRange>(typed.Reason);
  }

  [Fact]
  public void Resolve_UnactionableFrame_IsATypedMiss()
  {
    FrameRegistry registry = new();
    ComputerFrameRef frame = registry.Add(100, 100, Rect, actionable: false);

    FrameCoordinateResult miss = registry.ResolveForCoordinate(frame.FrameId, 50, 50);
    FrameCoordinateResult.Miss typed = Assert.IsType<FrameCoordinateResult.Miss>(miss);
    _ = Assert.IsType<FrameResolutionMiss.UnactionableFrame>(typed.Reason);
  }

  [Fact]
  public void Latest_ReturnsTheNewestFrame_AndNullWhenEmpty()
  {
    FrameRegistry registry = new();
    Assert.Null(registry.Latest());

    _ = registry.Add(10, 10, Rect, actionable: true);
    ComputerFrameRef second = registry.Add(20, 20, Rect, actionable: true);

    Assert.Equal(second.FrameId, registry.Latest()!.FrameId);
  }
}
