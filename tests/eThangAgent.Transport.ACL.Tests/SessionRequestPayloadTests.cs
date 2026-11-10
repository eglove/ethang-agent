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

  /// <summary>THE WIRE PAYLOAD NAMES NO OWNER AND NO DEPTH (issue #338). The issue's acceptance
  ///     states this as a grep over <c>src</c>, which no longer distinguishes a regression from
  ///     prose; this is the same rule as a property of the type, so a member that put a
  ///     relationship on the wire fails here whatever it is called.
  ///     <para>THE MEMBER LIST IS EXACT, not a blocklist: a new member has to be added here, and
  ///     adding one that carries ancestry is a decision this test forces into the open.</para></summary>
  [Fact]
  public void SessionRequestPayload_CarriesPlacementFactsOnly()
  {
    string[] members = [.. typeof(SessionRequestPayload).GetProperties()
        .Select(p => p.Name)
        .OrderBy(n => n, StringComparer.Ordinal)];

    Assert.Equal(
        ["ChainStep", "HandoffSummary", "Label", "Model", "Prompt", "RequestId", "Resumed",
            "SiblingOfWorkspaceId"],
        members);
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
