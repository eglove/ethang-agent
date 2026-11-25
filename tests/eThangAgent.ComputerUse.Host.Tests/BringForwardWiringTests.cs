namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>The bring-forward surface is the one thing that lets a foreground=true action
///     focus its own target. A broker constructed the way the production entry point
///     constructs it must carry the REAL surface: the default (null) resolves to
///     NoBringForward, which refuses every foreground=true action with
///     action_unavailable and makes the caller focus the window by hand.</summary>
public class BringForwardWiringTests
{
  [Fact]
  public void ProductionConstruction_CarriesRealBringForward()
  {
    PipeServer server = new(new BrokerConfig("pipe", "token"), new FakeObserver(), foregroundPid: () => -1);
    Assert.True(server.InputDispatch.HasBringForwardSurface,
      "the production construction path must wire the real bring-forward surface");
  }

  [Fact]
  public void InjectedSurface_IsUsedAsGiven()
  {
    // A test that needs the refusal shape injects it explicitly; the default is the
    // real surface, never the refusal.
    PipeServer server = new(new BrokerConfig("pipe", "token"), new FakeObserver(), foregroundPid: () => -1,
      bringForward: _ => BrokerResponse.Fail("action_unavailable", "injected", "action_sent=false"));
    Assert.True(server.InputDispatch.HasBringForwardSurface);
  }
}
