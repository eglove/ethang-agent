using eThangAgent.AgentDomain;

namespace eThangAgent.Composition;

/// <summary>Translates the Agent Domain's in-process chain events into
///     <see cref="ChainChangedEvent"/> facts on the session's agent event stream, so ONE
///     subscribable serves every reader that needs to know a chain moved (issue #312):
///     <list type="bullet">
///     <item>the workbench's Chains view, which must re-render instantly rather than poll</item>
///     <item>a future reader of the same fact (the status-line badge's count, #312 D6)</item>
///     </list>
///     <para>WHY A TRANSLATION AND NOT A DIRECT PUBLISH: <see cref="ChainService"/> raises
///     plain <c>Action&lt;T&gt;</c> events and lives in the Agent Domain, which owns the event
///     family but must not know about a session's stream instance. Publishing from HERE also
///     means the fact rides the container's own <see cref="IAgentEvents"/>, which the ChildHost
///     already forwards to the app over the wire (issue #245) - so a chain the model advances in
///     the host process reaches the app's view with no extra plumbing.</para>
///     <para>CONSTRUCT EAGERLY. DI is lazy and nothing else resolves this type, so a plain
///     registration would never subscribe and the events would fire into no listener - the exact
///     defect <see cref="PlanChangePublisher"/> exists to close.</para>
///     <para>The authoring agent is resolved through the constructor's author delegate, lazily at
///     publish time, for the reason the plan publisher states: a child run is ambient while a
///     root write is not, and the resolution must reflect whichever run is writing.</para></summary>
public sealed class ChainChangePublisher : IDisposable
{
  private readonly ChainService _chains;
  private readonly IAgentEvents _events;
  private readonly Func<AgentId> _author;

  public ChainChangePublisher(ChainService chains, IAgentEvents events, Func<AgentId> author)
  {
    _chains = chains ?? throw new ArgumentNullException(nameof(chains));
    _events = events ?? throw new ArgumentNullException(nameof(events));
    _author = author ?? throw new ArgumentNullException(nameof(author));

    _chains.Created += OnCreated;
    _chains.StepChanged += OnStepChanged;
    _chains.StatusChanged += OnStatusChanged;
  }

  private void OnCreated(ChainCreated e) => Publish(e.ChainId, "created", status: null, e.At);

  private void OnStepChanged(ChainStepChanged e) => Publish(e.ChainId, "step", status: null, e.At);

  /// <summary>A status move publishes the NEW status, because the park/completion distinction is
  ///     the whole point of the fact reaching a surface at all: a view told only 'something
  ///     changed' would re-read the record to learn what a subscriber could have been told.
  ///     <para>The park REASON is deliberately absent - see <see cref="ChainChangedEvent"/>.</para>
  ///     </summary>
  private void OnStatusChanged(ChainStatusChanged e)
      => Publish(e.ChainId, "status", e.To.ToString(), e.At);

  /// <summary>Publishes one fact. Faults are contained at the stream (the event family's own
  ///     contract), so a subscriber fault can never reach the chain write that raised it - a
  ///     chain mutation must not fail because a view could not render it.</summary>
  private void Publish(int chainId, string change, string? status, DateTimeOffset at)
      => _events.Publish(new ChainChangedEvent(_author(), at, chainId, change, status));

  public void Dispose()
  {
    _chains.Created -= OnCreated;
    _chains.StepChanged -= OnStepChanged;
    _chains.StatusChanged -= OnStatusChanged;
  }
}
