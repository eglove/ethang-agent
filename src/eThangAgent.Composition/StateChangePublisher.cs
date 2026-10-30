using eThangAgent.AgentDomain;
using eThangAgent.StateDomain;

namespace eThangAgent.Composition;

/// <summary>Translates the State Domain's in-process change bus into
///     <see cref="StateChangedEvent"/> facts on the session's agent event stream, so ONE
///     subscribable serves every reader that needs to know a state key moved (issue #318):
///     <list type="bullet">
///     <item>the workbench's ledger view, which must re-render instantly rather than poll</item>
///     <item>a parent agent waiting with <c>wakeOn=state</c> (that path already exists and is
///     untouched - the wake adapter subscribes the bus directly, in-process)</item>
///     </list>
///     <para>WHY THIS EXISTS AT ALL, when the wake adapter already subscribes the bus:
///     <see cref="IStateChangeBus"/> is IN-PROCESS and session-scoped, while every production
///     turn runs in the ChildHost process. A rail view subscribes the APP container's stream,
///     so a bus-only subscription would listen to a bus that nothing publishes on and stay
///     silently stale forever - the exact defect <see cref="PlanChangePublisher"/> and
///     <see cref="ChainChangePublisher"/> exist to close, one record later.</para>
///     <para>CONSTRUCT EAGERLY. DI is lazy and nothing resolves this type by name, so a plain
///     registration would never subscribe and every state change would fire into no listener.
///     </para>
///     <para>METADATA ONLY: the key and the change kind travel, never the stored value. A
///     ledger can be a hundred kilobytes, and announcing that one line joined it must not put
///     the whole record on the wire.</para></summary>
public sealed class StateChangePublisher : IDisposable
{
  private readonly IAgentEvents _events;
  private readonly Func<AgentId> _author;
  private readonly IDisposable _lease;

  public StateChangePublisher(IStateChangeBus bus, IAgentEvents events, Func<AgentId> author)
  {
    ArgumentNullException.ThrowIfNull(bus);
    _events = events ?? throw new ArgumentNullException(nameof(events));
    _author = author ?? throw new ArgumentNullException(nameof(author));
    _lease = bus.Subscribe(OnChange);
  }

  /// <summary>Publishes one fact per change. Faults are contained at the stream (the event
  ///     family's own contract), so a subscriber fault can never reach the state write that
  ///     raised it - a state mutation must not fail because a view could not render it.
  ///     </summary>
  /// <param name="change">The change the bus reported.</param>
  private void OnChange(StateChange change)
  {
    ArgumentNullException.ThrowIfNull(change);
    _events.Publish(new StateChangedEvent(_author(), change.At, change.Key, TagFor(change.Kind)));
  }

  /// <summary>The change kind as the short tag the fact carries. The set is closed, so the
  ///     fallback names a defect rather than guessing - the <c>ChainService.KindOf</c> precedent.
  ///     </summary>
  /// <param name="kind">The bus's change kind.</param>
  private static string TagFor(StateChangeKind kind) => kind switch
  {
    StateChangeKind.Set => "set",
    StateChangeKind.Append => "append",
    StateChangeKind.Delete => "delete",
    _ => "unknown",
  };

  public void Dispose() => _lease.Dispose();
}
