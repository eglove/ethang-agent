
namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>Fix-round I3b: surface classification (dialog/open/save panel vs window by
///     class-name heuristics) and lifecycle (replaced|closed|stable by comparing window
///     handles across captures) must be real, fake-testable logic.</summary>
public class SurfaceClassificationTests
{
  [Theory]
  [InlineData("#32770", SurfaceKind.Dialog)]
  [InlineData("Crypto dudes dialog", SurfaceKind.Dialog)]
  [InlineData("Notepad", SurfaceKind.Window)]
  public void ClassNames_MapToSurfaceKinds(string className, SurfaceKind expected) =>
    Assert.Equal(expected, SurfaceClassifier.KindFor(className));

  [Fact]
  public void NewHandle_ReplacingOld_IsReplaced()
  {
    SurfaceTracker tracker = new();
    _ = tracker.Observe(appKey: "p1", windowHandle: 111, SurfaceKind.Window);
    SurfaceLifecycle lifecycle = tracker.Observe(appKey: "p1", windowHandle: 222, SurfaceKind.Window);
    Assert.Equal(SurfaceLifecycle.Replaced, lifecycle);
  }

  [Fact]
  public void SameHandle_IsStable()
  {
    SurfaceTracker tracker = new();
    _ = tracker.Observe(appKey: "p1", windowHandle: 111, SurfaceKind.Window);
    SurfaceLifecycle lifecycle = tracker.Observe(appKey: "p1", windowHandle: 111, SurfaceKind.Window);
    Assert.Equal(SurfaceLifecycle.Stable, lifecycle);
  }

  [Fact]
  public void HandleDisappears_IsClosed()
  {
    SurfaceTracker tracker = new();
    _ = tracker.Observe(appKey: "p1", windowHandle: 111, SurfaceKind.Window);
    SurfaceLifecycle lifecycle = tracker.Observe(appKey: "p1", windowHandle: 0, SurfaceKind.Window);
    Assert.Equal(SurfaceLifecycle.Closed, lifecycle);
  }
};
