using System.Text.Json;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>The session-request payload record (issue #234) survives a serialize/deserialize
///     round trip with every member intact. The payload rides the event's string Payload as
///     JSON (the codec stays string-payload; parsing is the consumer's job, per the
///     RootTurnToolPayload precedent), so the record's wire shape is what these tests pin.</summary>
public class SessionRequestPayloadTests
{
  [Fact]
  public void SessionRequestPayload_SerializesAndRoundTrips()
  {
    // A plain launch carries no handoff summary; the round trip must preserve the null.
    SessionRequestPayload payload = new("req-1", "continue the work", null);

    string json = JsonSerializer.Serialize(payload);
    SessionRequestPayload? parsed = JsonSerializer.Deserialize<SessionRequestPayload>(json);

    Assert.NotNull(parsed);
    Assert.Equal(payload, parsed);
    Assert.Null(parsed.HandoffSummary);
  }

  [Fact]
  public void SessionRequestPayload_TheHandoffSummary_RoundTrips()
  {
    // A compaction handoff carries its summary text; it must survive the wire verbatim.
    SessionRequestPayload payload = new("req-2", "continue the work", "handoff summary text");

    string json = JsonSerializer.Serialize(payload);
    SessionRequestPayload? parsed = JsonSerializer.Deserialize<SessionRequestPayload>(json);

    Assert.NotNull(parsed);
    Assert.Equal(payload, parsed);
    Assert.Equal("handoff summary text", parsed.HandoffSummary);
  }
}
