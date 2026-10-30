using eThangAgent.AgentDomain;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>Issue #92: the receipt facts must survive the wire. Every production
///     conversation runs in a child host, and the app-side ledger only ever sees what the
///     host forwarded — so a payload that loses the message ids would leave every remote
///     session's receipts stuck at "queued" no matter what the child actually did.
///     <see cref="ChildEventEnvelope"/> serializes the event by its runtime type and
///     decodes by kind name, so a new kind must be registered AND round-trip its ids.</summary>
public class ReceiptEventWireTests
{
  [Fact]
  public void DeliveredEvent_RoundTripsItsMessageId()
  {
    AgentId child = AgentId.NewId();
    Guid messageId = Guid.NewGuid();
    MessageDeliveredEvent original = new(child, DateTimeOffset.UtcNow, "inbound", 1, 42, messageId);

    ChildEvent? decoded = ChildEventEnvelope.From(original).Decode();

    MessageDeliveredEvent restored = Assert.IsType<MessageDeliveredEvent>(decoded);
    Assert.Equal(messageId, restored.MessageId);
    Assert.Equal(child, restored.ChildId);
    Assert.Equal("inbound", restored.Direction);
    Assert.Equal(1, restored.Urgency);
    Assert.Equal(42, restored.Size);
  }

  [Fact]
  public void DrainedEvent_RoundTripsItsMessageIds_AndDerivesTheCount()
  {
    AgentId child = AgentId.NewId();
    Guid first = Guid.NewGuid();
    Guid second = Guid.NewGuid();
    MailboxDrainedEvent original = new(child, DateTimeOffset.UtcNow, [first, second]);

    ChildEvent? decoded = ChildEventEnvelope.From(original).Decode();

    MailboxDrainedEvent restored = Assert.IsType<MailboxDrainedEvent>(decoded);
    Assert.Equal([first, second], restored.MessageIds);
    Assert.Equal(2, restored.Count);
  }

  [Fact]
  public void UndeliveredEvent_IsRegisteredAndRoundTrips()
  {
    AgentId child = AgentId.NewId();
    Guid messageId = Guid.NewGuid();
    MailboxUndeliveredEvent original = new(child, DateTimeOffset.UtcNow, [messageId]);

    ChildEventEnvelope envelope = ChildEventEnvelope.From(original);
    ChildEvent? decoded = envelope.Decode();

    // An unregistered kind decodes to null and is silently dropped by the pump - the
    // exact way a remote session's receipts would go dark.
    Assert.Equal(nameof(MailboxUndeliveredEvent), envelope.EventKind);
    MailboxUndeliveredEvent restored = Assert.IsType<MailboxUndeliveredEvent>(decoded);
    Assert.Equal([messageId], restored.MessageIds);
    Assert.Equal(child, restored.ChildId);
  }

  /// <summary>A pre-handshake payload carries no MessageId; it decodes as Guid.Empty, and
  ///     the ledger then records no receipt for it (never a fabricated handle).</summary>
  [Fact]
  public void DeliveredEvent_WithoutAMessageId_DecodesAsEmpty()
  {
    AgentId child = AgentId.NewId();
    // Exactly what a pre-handshake peer emits: the real payload with the MessageId member
    // removed (derived, not hand-written, so it cannot drift from the wire shape).
    string current = ChildEventEnvelope.From(
        new MessageDeliveredEvent(child, DateTimeOffset.UtcNow, "inbound", 0, 5, Guid.NewGuid())).EventJson;
    using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(current);
    System.Text.Json.Nodes.JsonObject withoutId = [];
    foreach (System.Text.Json.JsonProperty property in doc.RootElement.EnumerateObject())
    {
      if (!string.Equals(property.Name, "MessageId", StringComparison.Ordinal))
      {
        withoutId[property.Name] = System.Text.Json.Nodes.JsonNode.Parse(property.Value.GetRawText());
      }
    }

    ChildEventEnvelope envelope = new(child.Value, nameof(MessageDeliveredEvent), withoutId.ToJsonString());

    MessageDeliveredEvent restored = Assert.IsType<MessageDeliveredEvent>(envelope.Decode());

    Assert.Equal(Guid.Empty, restored.MessageId);
  }

  /// <summary>Issue #312: the chain fact must survive the wire, for the reason the plan fact
  ///     must. Every production conversation runs in a host process, so a chain the model
  ///     advances there only reaches the app's Chains view if this kind is REGISTERED - an
  ///     unregistered kind decodes to null and is silently dropped by the pump, which is exactly
  ///     how the rail would go dark in production while every unit test passed.</summary>
  [Fact]
  public void ChainChangedEvent_IsRegisteredAndRoundTrips()
  {
    AgentId author = AgentId.NewId();
    ChainChangedEvent original = new(author, DateTimeOffset.UtcNow, 7, "status", "Parked");

    ChildEventEnvelope envelope = ChildEventEnvelope.From(original);
    ChildEvent? decoded = envelope.Decode();

    Assert.Equal(nameof(ChainChangedEvent), envelope.EventKind);
    ChainChangedEvent restored = Assert.IsType<ChainChangedEvent>(decoded);
    Assert.Equal(7, restored.ChainId);
    Assert.Equal("status", restored.Change);
    Assert.Equal("Parked", restored.Status);
    Assert.Equal(author, restored.ChildId);
  }

  /// <summary>A step fact carries no status: the change tag says a step moved and the STORE says
  ///     what it moved to, so a null status must round-trip as null rather than as an empty
  ///     string a view would render as a status it does not have.</summary>
  [Fact]
  public void ChainStepChangedEvent_RoundTripsANullStatus()
  {
    ChainChangedEvent original = new(AgentId.NewId(), DateTimeOffset.UtcNow, 3, "step", null);

    ChainChangedEvent restored = Assert.IsType<ChainChangedEvent>(
        ChildEventEnvelope.From(original).Decode());

    Assert.Null(restored.Status);
  }

  /// <summary>Issue #318: the state fact must survive the wire, for the reason the plan and chain
  ///     facts must. Every production conversation runs in a host process, and the ledger is
  ///     APPENDED there by the experiment tool - so a ledger write only reaches the app's ledger
  ///     view if this kind is REGISTERED. An unregistered kind decodes to null and is silently
  ///     dropped by the pump, which is exactly how the rail would go dark in production while
  ///     every unit test passed.</summary>
  [Fact]
  public void StateChangedEvent_IsRegisteredAndRoundTrips()
  {
    AgentId author = AgentId.NewId();
    StateChangedEvent original = new(author, DateTimeOffset.UtcNow, "sci.my-plan/ledger", "append");

    ChildEventEnvelope envelope = ChildEventEnvelope.From(original);
    ChildEvent? decoded = envelope.Decode();

    Assert.Equal(nameof(StateChangedEvent), envelope.EventKind);
    StateChangedEvent restored = Assert.IsType<StateChangedEvent>(decoded);
    Assert.Equal("sci.my-plan/ledger", restored.Key);
    Assert.Equal("append", restored.Change);
    Assert.Equal(author, restored.ChildId);
  }

  /// <summary>The state fact carries METADATA ONLY: a ledger can be a hundred kilobytes, so
  ///     announcing that one line joined it must not put the whole record on the wire. Pinned
  ///     against the type's own shape, because a member added later would silently start
  ///     shipping the value.</summary>
  [Fact]
  public void StateChangedEvent_CarriesNoStoredValue()
  {
    string[] members = [.. typeof(StateChangedEvent).GetProperties().Select(p => p.Name)];

    Assert.Equal(["At", "Change", "ChildId", "Key"], members.OrderBy(n => n, StringComparer.Ordinal));
  }
}
