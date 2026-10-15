using System.Globalization;

namespace eThangAgent.AgentDomain;

/// <summary>Translates the child-event stream into wake signals on the session's
///     WaitWakeHub. Progress events COALESCE per child (spec decision 3): a wake fires
///     only when a child's phase/label changes, never on identical repeated beats —
///     the full stream stays visible to human surfaces; the parent just is not woken
///     for no-op repeats. Settled/started/preempted/drained events are not wake-worthy:
///     settlement ends the wait on its own, and mailbox drain/preemption carry their
///     own signals. Disposable: disposing unsubscribes from the stream.</summary>
public sealed class ChildEventWakeAdapter : IDisposable
{
  private readonly Lock _gate = new();
  private readonly Dictionary<AgentId, (ChildPhase Phase, string Label)> _lastProgress = [];
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
      case ChildProgressEvent progress:
        bool wake;
        lock (_gate)
        {
          bool known = _lastProgress.TryGetValue(progress.ChildId, out (ChildPhase Phase, string Label) last);
          wake = !known || last.Phase != progress.Phase || last.Label != progress.Label;
          if (wake)
          {
            _lastProgress[progress.ChildId] = (progress.Phase, progress.Label);
          }
        }

        if (wake)
        {
          _hub.Register(new WakeSignal(WakeReason.Progress, progress.ChildId, progress.At,
              string.Create(CultureInfo.InvariantCulture, $"progress {progress.Phase} {progress.Label} child={progress.ChildId}")));
        }

        break;
      case ChildIdleAlertEvent idle:
        _hub.Register(new WakeSignal(WakeReason.Idle, idle.ChildId, idle.At,
            string.Create(CultureInfo.InvariantCulture, $"idle {(int)idle.IdleAge.TotalSeconds}s last={idle.LastPhase} child={idle.ChildId}")));
        break;
      case ChildBudgetAlertEvent budget:
        _hub.Register(new WakeSignal(WakeReason.Budget, budget.ChildId, budget.At,
            string.Create(CultureInfo.InvariantCulture, $"budget {budget.BudgetKind} consumed={budget.Consumed} child={budget.ChildId}")));
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
