using eThangAgent.AgentDomain;

namespace eThangAgent.Agent.Application;

/// <summary>The supervisor registry feed: translates a container's child-event stream
///     into supervisor facts (D6). The per-kind contract is pinned by
///     SupervisorFeedContractTests (W1.3) — decided once, in the open:
///     - ChildProgressEvent: a heartbeat plus a phase (the loop emits it at every
///       safe point).
///     - ChildStartedEvent: a heartbeat — the runtime registers a FRESH supervisor at
///       every (re)start, so the retry's started event lands on that new instance as
///       the liveness fact of a live run.
///     - ChildBudgetAlertEvent: NO beat — a budget alert is not progress (near-zero
///       burn while alerts fire is the watchdog's strongest stuck evidence), and it is
///       published from inside the supervisor's non-reentrant lock, which a self-echo
///       would deadlock.
///     - PreemptedEvent: NO beat — preemption interrupts the receiver immediately
///       after publish; the interrupt must be allowed to stall the idle window.
///     - MessageDeliveredEvent: NO beat — mailbox lifecycle is not run progress.
///     - ChildIdleAlertEvent: NEVER feeds — it is published from inside CheckIdle
///       under the same lock; a beat there would self-deadlock AND clear the very
///       alert being raised, stalling the graduated response at the first breach.
///     - ChildSettledEvent: retires the child's supervisor.
///     Without this feed a supervisor's idle clock starts at OnStart and never
///     refreshes, so ANY healthy child outliving the idle threshold false-positives as
///     hung — the defect this class exists to close, shared by the app-side watchdog
///     and the ChildHost's.</summary>
public sealed class SupervisorFeed(ChildSupervisorRegistry supervisors) : IAgentEventSubscriber
{
  public void OnEvent(ChildEvent evt)
  {
    ArgumentNullException.ThrowIfNull(evt);
    switch (evt)
    {
      case ChildProgressEvent progress:
        if (supervisors.Find(progress.ChildId) is { } fed)
        {
          fed.OnBeat();
          fed.OnPhase(progress.Phase);
        }

        break;
      case ChildStartedEvent started:
        if (supervisors.Find(started.ChildId) is { } begun)
        {
          begun.OnBeat(); // fresh-supervisor liveness; phase stays the OnStart default
        }

        break;
      case ChildSettledEvent:
        supervisors.Unregister(evt.ChildId);
        break;
      case ChildIdleAlertEvent:
        break; // never feeds: see the contract above — lock reentrancy AND alert preservation
      default:
        break; // budget alerts, preemptions, mail deliveries: no supervision facts
    }
  }
}
