using eThangAgent.AgentDomain;

namespace eThangAgent.Desktop;

/// <summary>W4.4: one open tab's unread-steering badge, computed by SUBSCRIPTION,
///     never by sampling: MessageDelivered raises the count (inbound and
///     cross-container alike - direction is metadata, not a different fact),
///     MailboxDrained and ChildSettled clear it. Every other event kind is ignored:
///     the badge is mailbox state, not run state. The badge tracks deliveries to
///     one bound agent id (the session's root at attach time); foreign ids never
///     move it. Push-only by construction - no timer ever samples a mailbox (the
///     no-new-polling doctrine). The UI callback is wrapped in a fault boundary so
///     a rendering hiccup can never propagate into the child loop (the stream
///     contains subscriber faults; this class contains its own).</summary>
internal sealed class UnreadBadgeSubscriber(AgentId id, Action<int> render) : IAgentEventSubscriber
{
  private readonly AgentId _id = id;
  private Action<int> _render = render ?? throw new ArgumentNullException(nameof(render));
  private int _count;

  /// <summary>Re-points the render callback: the badge view-model wires its own apply
  ///     after construction, wrapped for the UI thread by the shell's marshal. A
  ///     transient no-op sink (the Attach-time constructor argument) keeps that
  ///     construction order safe.</summary>
  public void ChangeSink(Action<int> render)
  {
    ArgumentNullException.ThrowIfNull(render);
    _render = render;
  }

  /// <summary>The current unread count (the badge's value).</summary>
  public int Count => Interlocked.CompareExchange(ref _count, 0, 0);

  public void OnEvent(ChildEvent evt)
  {
    ArgumentNullException.ThrowIfNull(evt);
    if (!evt.ChildId.Equals(_id))
    {
      return; // another session's child: never this badge's business
    }

    switch (evt)
    {
      case MessageDeliveredEvent:
        _ = Interlocked.Increment(ref _count);
        Render();
        break;
      case MailboxDrainedEvent:
      case ChildSettledEvent:
        if (Interlocked.Exchange(ref _count, 0) != 0)
        {
          Render();
        }

        break;
      default:
        break; // progress, budget, idle, started: the badge takes no facts from them
    }
  }

  private void Render()
  {
    // Named decision (CA1031): the badge is presentation - a fault in the render
    // callback must not take down the event stream's other subscribers.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      _render(_count);
    }
    catch
    {
      // Swallowed deliberately: see the named decision above.
    }
#pragma warning restore CA1031
  }
}
