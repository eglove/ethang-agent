using eThangAgent.AgentDomain;
using eThangAgent.PlanDomain;

namespace eThangAgent.Composition;

/// <summary>Translates the Plan Domain's in-process events into <see cref="PlanChangedEvent"/>
///     facts on the session's agent event stream, so ONE subscribable serves every reader
///     that needs to know a plan moved:
///     <list type="bullet">
///     <item>the workbench's Plans view, which must re-render instantly rather than poll</item>
///     <item>a parent agent waiting on a child with <c>wakeOn=plan</c>, which must see a
///     subagent mark a step done</item>
///     </list>
///     <para>WHY A TRANSLATION AND NOT A DIRECT PUBLISH: the Plan Domain references only
///     Capability and SharedKernel, so it cannot reference the Agent Domain's event family.
///     The same split the state-change adapter honors. Publishing from HERE also means the
///     fact rides the container's own <see cref="IAgentEvents"/>, which the ChildHost
///     already forwards to the app over the wire (issue #245) - so a plan written by the
///     model in the host process reaches the app's view with no extra plumbing.</para>
///     <para>CONSTRUCT EAGERLY. DI is lazy and nothing else resolves this type, so a plain
///     registration would never subscribe and the events would fire into no listener -
///     exactly the defect this class exists to close.</para>
///     <para>The authoring agent is resolved through the constructor's author delegate,
///     lazily at
///     publish time: a child run is ambient (<c>SubAgentSpawner.RunningChild</c>) while a
///     root write is not, and the resolution must reflect whichever run is writing rather
///     than whichever container happened to build first.</para></summary>
public sealed class PlanChangePublisher : IDisposable
{
  private readonly PlanService _plans;
  private readonly IAgentEvents _events;
  private readonly Func<AgentId> _author;

  public PlanChangePublisher(PlanService plans, IAgentEvents events, Func<AgentId> author)
  {
    _plans = plans ?? throw new ArgumentNullException(nameof(plans));
    _events = events ?? throw new ArgumentNullException(nameof(events));
    _author = author ?? throw new ArgumentNullException(nameof(author));

    _plans.Created += OnCreated;
    _plans.StepChanged += OnStepChanged;
    _plans.StatusChanged += OnStatusChanged;
  }

  private void OnCreated(PlanCreated e) => Publish(e.PlanId, "created", status: null, e.At);

  private void OnStepChanged(PlanStepChanged e) => Publish(e.PlanId, "steps", status: null, e.At);

  private void OnStatusChanged(PlanStatusChanged e) => Publish(e.PlanId, "status", e.To.ToString(), e.At);

  /// <summary>Publishes one fact. Faults are contained at the stream (the event family's
  ///     own contract), so a subscriber fault can never reach the plan write that raised
  ///     it - a plan mutation must not fail because a view could not render it.</summary>
  private void Publish(int planId, string change, string? status, DateTimeOffset at)
      => _events.Publish(new PlanChangedEvent(_author(), at, planId, change, status));

  public void Dispose()
  {
    _plans.Created -= OnCreated;
    _plans.StepChanged -= OnStepChanged;
    _plans.StatusChanged -= OnStatusChanged;
  }
}
