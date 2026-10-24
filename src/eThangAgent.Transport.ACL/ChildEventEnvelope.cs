using System.Text.Json;
using eThangAgent.AgentDomain;

namespace eThangAgent.Transport.ACL;

/// <summary>One child-event envelope forwarded from the conversation host to the app
///     (issue #245): the host subscribes a forwarder to every child container's event
///     stream and republishes what it sees as <c>child-event</c> envelopes; the app-side
///     runtime re-publishes them onto the session's own <see cref="IAgentEvents"/> stream,
///     so the Desktop's subagent panel, unread badge, and wait-wake adapters observe
///     REMOTE children exactly like in-process ones.
/// <para>The payload is the serialized <see cref="ChildEvent"/> itself - the event family
///     is metadata-only by design (D5: ids, timestamps, counts, enums, short labels; never
///     message or report content), so the wire carries no transcript content. The envelope
///     carries the child's id beside the payload so a receiver can route without parsing
///     the event JSON.</para></summary>
public sealed record ChildEventEnvelope(Guid ChildId, string EventKind, string EventJson)
{
  /// <summary>Serializes one child event into its wire envelope. The event's own JSON
  ///     is the carrier: <see cref="ChildEvent"/> records are plain serializable shapes
  ///     (ids, enums, timestamps), and the envelope's ChildId duplicates the event's
  ///     ChildId so the receiver's routing never depends on the payload decoding.</summary>
  public static ChildEventEnvelope From(ChildEvent evt)
  {
    ArgumentNullException.ThrowIfNull(evt);
    return new ChildEventEnvelope(evt.ChildId.Value, evt.GetType().Name, JsonSerializer.Serialize(evt, evt.GetType()));
  }

  /// <summary>Decodes the payload back into a typed child event, or null when the
  ///     payload names a type this build does not know - a forward-compatible peer's
  ///     new event kind is dropped here rather than crashing the pump (the same
  ///     forward-compatibility rule the envelope kinds follow).</summary>
  public ChildEvent? Decode()
  {
    Type? eventType = EventTypeFor(EventKind);

    if (eventType is null)
    {
      return null;
    }

    try
    {
      return (ChildEvent?)JsonSerializer.Deserialize(EventJson, eventType);
    }
#pragma warning disable CA1031 // Named decision: an undecodable payload is a peer fault, not a pump crash.
    catch (Exception)
#pragma warning restore CA1031 // Do not catch general exception types
    {
      return null;
    }
  }

  /// <summary>The wire's event vocabulary: the event-kind name to its concrete type.
  ///     The one place the map lives, so a new event kind is registered exactly once.
  ///     Null for an unknown kind - a forward-compatible peer's new event.</summary>
  private static readonly Dictionary<string, Type> EventTypes = new(StringComparer.Ordinal)
  {
    [nameof(ChildStartedEvent)] = typeof(ChildStartedEvent),
    [nameof(ChildProgressEvent)] = typeof(ChildProgressEvent),
    [nameof(ChildSettledEvent)] = typeof(ChildSettledEvent),
    [nameof(ChildIdleAlertEvent)] = typeof(ChildIdleAlertEvent),
    [nameof(ChildBudgetAlertEvent)] = typeof(ChildBudgetAlertEvent),
    [nameof(ChildStreamStalledEvent)] = typeof(ChildStreamStalledEvent),
    [nameof(MessageDeliveredEvent)] = typeof(MessageDeliveredEvent),
    [nameof(MailboxDrainedEvent)] = typeof(MailboxDrainedEvent),
    [nameof(MailboxUndeliveredEvent)] = typeof(MailboxUndeliveredEvent),
    [nameof(PreemptedEvent)] = typeof(PreemptedEvent),
  };

  private static Type? EventTypeFor(string eventKind)
      => EventTypes.TryGetValue(eventKind, out Type? eventType) ? eventType : null;

}
