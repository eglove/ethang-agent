namespace eThangAgent.AgentDomain;

/// <summary>The no-op event stream: publishes nowhere, subscribes nobody. Legacy/optional
///     wiring uses this instead of null checks at every publish site.</summary>
public sealed class NullAgentEvents : IAgentEvents
{
  public static readonly NullAgentEvents Instance = new();

  public IDisposable Subscribe(IAgentEventSubscriber subscriber)
      => new FakeLease();

  public void Publish(ChildEvent evt)
  {
  }

  private sealed class FakeLease : IDisposable
  {
    public void Dispose()
    {
    }
  }
}
