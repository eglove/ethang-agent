using System.Globalization;

namespace eThangAgent.AgentDomain;

/// <summary>Translates the child-event stream into wake signals on the session's
///     WaitWakeHub. Progress events are NOT wake-worthy: a child's progress beats fire
///     on every tool call, so a wait that woke on them never rests — the beat stream
///     stays visible to human surfaces (the Desktop panel) and feeds the watchdog's
///     idle clock, but it never wakes a wait. Settled/started/preempted/drained events
///     are not wake-worthy either: settlement ends the wait on its own, and mailbox
///     drain/preemption carry their own signals. Disposable: disposing unsubscribes
///     from the stream.</summary>
public sealed class ChildEventWakeAdapter : IDisposable
{
  private readonly WaitWakeHub _hub;
  private readonly IDisposable _lease;

  public ChildEventWakeAdapter(IAgentEvents events, WaitWakeHub hub)
  {
    ArgumentNullException.ThrowIfNull(events);
    _hub = hub ?? throw new ArgumentNullException(nameof(hub));
    _lease = events.Subscribe(new Subscriber(this));
  }

  /// <summary>Unsubscribes from the event stream. Idempotent.</summary>
  public void Dispose() => _lease.Dispose();

  private void OnEvent(ChildEvent evt)
  {
    switch (evt)
    {
      case ChildIdleAlertEvent idle:
        _hub.Register(new WakeSignal(WakeReason.Idle, idle.ChildId, idle.At,
            string.Create(CultureInfo.InvariantCulture, $"idle {(int)idle.IdleAge.TotalSeconds}s last={idle.LastPhase} child={idle.ChildId}")));
        break;
      case ChildBudgetAlertEvent budget:
        _hub.Register(new WakeSignal(WakeReason.Budget, budget.ChildId, budget.At,
            string.Create(CultureInfo.InvariantCulture, $"budget {budget.BudgetKind} consumed={budget.Consumed} child={budget.ChildId}")));
        break;
      case ChildStreamStalledEvent stalled:
        _hub.Register(new WakeSignal(WakeReason.StreamStall, stalled.ChildId, stalled.At,
            string.Create(CultureInfo.InvariantCulture, $"stream-stalled quiet={(int)stalled.QuietFor.TotalSeconds}s last={stalled.LastActivityKind} child={stalled.ChildId}")));
        break;
      case MessageDeliveredEvent mail:
        _hub.Register(new WakeSignal(WakeReason.Mailbox, mail.ChildId, mail.At,
            string.Create(CultureInfo.InvariantCulture, $"mail delivered dir={mail.Direction} child={mail.ChildId}")));
        break;
      default:
        break; // settled, started, preempted, drained: not wake-worthy
    }
  }

  private sealed class Subscriber(ChildEventWakeAdapter owner) : IAgentEventSubscriber
  {
    public void OnEvent(ChildEvent evt) => owner.OnEvent(evt);
  }
}
