using eThangAgent.ModelDomain;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>The <c>context</c> wire kind (issue #255): the session's provider-scored
///     accounting travels from the host process to the app, so the status bar's CTX line
///     advances for a host-run turn exactly as it did in-process.
///
///     Anti-tautology: the payload's writer and reader are the PRODUCTION ones
///     (<see cref="RootTurnContextPayload.Serialize"/> / <see cref="RootTurnContextPayload.Parse"/>)
///     and the event goes through the real <see cref="RootTurnProtocol"/> codec, so a
///     shape that drifted between the two sides fails here rather than in production.</summary>
public class RootTurnContextPayloadTests
{
  [Fact]
  public void Context_IsAKnownEventKind()
  {
    // The kind is added to the STRICT set, not smuggled through `progress`: a host that
    // emits one must reach an app that recognizes it, and an unknown kind must still
    // fail the protocol check.
    Assert.Contains("context", RootTurnProtocol.EventKinds);
    RootTurnEvent evt = new("t", "context", "{}");

    RootTurnEvent parsed = RootTurnProtocol.ParseEvent(RootTurnProtocol.SerializeEvent(evt));

    Assert.Equal("context", parsed.Kind);
  }

  [Fact]
  public void ParseEvent_AnUnknownKindStillThrows()
  {
    // Adding a kind must not loosen the boundary: a peer that invents one is a protocol
    // fault, never a forward-compatible unknown.
    const string json = /*lang=json,strict*/ "{\"TurnId\":\"t\",\"Kind\":\"contextual\",\"Payload\":\"\"}";

    _ = Assert.Throws<InvalidOperationException>(() => RootTurnProtocol.ParseEvent(json));
  }

  [Fact]
  public void Serialize_RoundTripsEveryFieldIncludingTheBreakdown()
  {
    ContextSnapshot snapshot = new(
        new ContextStatus(148_200, 300_000, 9_000, 1_000_000, 14.82),
        new ContextBreakdown(12_000, 120_100, 16_000));

    string payload = RootTurnContextPayload.Serialize(snapshot);
    RootTurnContextPayload parsed = Assert.IsType<RootTurnContextPayload>(
        RootTurnContextPayload.Parse(payload));

    Assert.Equal(snapshot, parsed.ToSnapshot());
  }

  [Fact]
  public void Serialize_NullBreakdown_StaysNull()
  {
    // An ABSENT breakdown (nothing reported yet) is distinct from an all-zero one: the
    // status line renders "" for the first and a zeroed hover line for the second.
    ContextSnapshot snapshot = new(new ContextStatus(null, 0, 0, null, null), null);

    RootTurnContextPayload parsed = Assert.IsType<RootTurnContextPayload>(
        RootTurnContextPayload.Parse(RootTurnContextPayload.Serialize(snapshot)));

    Assert.Null(parsed.Breakdown);
    Assert.Null(parsed.ToSnapshot().Breakdown);
  }

  [Fact]
  public void Parse_UnreadablePayload_ReturnsNull()
      // The app drops an unreadable context payload rather than faulting the pump: a
      // status readout is never worth a dead connection.
      => Assert.Null(RootTurnContextPayload.Parse("not json"));
}
