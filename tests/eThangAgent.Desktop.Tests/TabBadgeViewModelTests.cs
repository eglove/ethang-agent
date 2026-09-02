using eThangAgent.AgentDomain;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

/// <summary>W4.4: the tab view-model surfaces the unread count and pushes it from
///     the session's child-event stream subscription (never a poll). The marshal
///     wrapper sees every transition (the shell's UI-thread poster; the test's
///     recorder); the badge state itself flows through the view-model. The lease is
///     disposed with the tab so a closed tab's badge never renders again.</summary>
public class TabBadgeViewModelTests
{
  private sealed class CaptureStream : IAgentEvents
  {
    private readonly List<IAgentEventSubscriber> _subscribers = [];
    public IDisposable Subscribe(IAgentEventSubscriber subscriber)
    {
      _subscribers.Add(subscriber);
      return new Lease(_subscribers, subscriber);
    }

    public void Publish(ChildEvent evt)
    {
      foreach (IAgentEventSubscriber s in _subscribers.ToArray())
      {
        s.OnEvent(evt);
      }
    }

    private sealed class Lease(ICollection<IAgentEventSubscriber> subscribers, IAgentEventSubscriber self) : IDisposable
    {
      public void Dispose() => subscribers.Remove(self);
    }
  }

  /// <summary>Attaches with a recording marshal (identity apply + capture).</summary>
  private static (TabBadgeViewModel Badge, List<int> Rendered) Make(CaptureStream stream, AgentId root)
  {
    List<int> rendered = [];
    TabBadgeViewModel badge = TabBadgeViewModel.Attach(stream, root, apply => count =>
    {
      rendered.Add(count);
      apply(count);
    }) ?? throw new InvalidOperationException("attach returned null for a live stream");
    return (badge, rendered);
  }

  [Fact]
  public void Delivered_RaisesTheBadge_DrainClearsIt()
  {
    CaptureStream stream = new();
    AgentId root = new(Guid.NewGuid());
    (TabBadgeViewModel badge, List<int> rendered) = Make(stream, root);

    stream.Publish(new MessageDeliveredEvent(root, DateTimeOffset.UtcNow, "inbound", 0, 8));
    Assert.Equal(1, badge.UnreadCount);
    Assert.True(badge.HasUnread);

    stream.Publish(new MailboxDrainedEvent(root, DateTimeOffset.UtcNow, 1));
    Assert.Equal(0, badge.UnreadCount);
    Assert.False(badge.HasUnread);
    Assert.Equal([1, 0], rendered); // pushed: exactly the two transitions, nothing polled
  }

  [Fact]
  public void Detach_StopsRendering()
  {
    CaptureStream stream = new();
    AgentId root = new(Guid.NewGuid());
    (TabBadgeViewModel badge, List<int> rendered) = Make(stream, root);
    stream.Publish(new MessageDeliveredEvent(root, DateTimeOffset.UtcNow, "inbound", 0, 8));
    Assert.Equal(1, badge.UnreadCount);

    badge.Dispose();
    stream.Publish(new MessageDeliveredEvent(root, DateTimeOffset.UtcNow, "inbound", 0, 8));

    Assert.Equal(1, badge.UnreadCount); // frozen: the detached badge takes no further events
    Assert.Equal([1], rendered); // and rendered nothing after dispose
  }

  [Fact]
  public void HeadlessHosts_NeverConstructAnything()
    => Assert.Null(TabBadgeViewModel.Attach(null, new AgentId(Guid.NewGuid())));
}
