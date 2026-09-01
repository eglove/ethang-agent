using eThangAgent.AgentDomain;

namespace eThangAgent.Agent.Application;

/// <summary>The supervisor registry feed: translates a container's child-event stream
///     into supervisor facts (D6). A ChildProgressEvent is a heartbeat (the loop emits
///     it at every safe point) plus a phase; a settle retires the child's supervisor.
///     Without this feed a supervisor's idle clock starts at OnStart and never
///     refreshes, so ANY healthy child outliving the idle threshold false-positives as
///     hung — the defect this class exists to close, shared by the app-side watchdog
///     and the ChildHost's (handoff item 2).</summary>
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
      case ChildSettledEvent:
        supervisors.Unregister(evt.ChildId);
        break;
      default:
        break; // lifecycle/alert events carry no supervision facts (yet)
    }
  }
}
