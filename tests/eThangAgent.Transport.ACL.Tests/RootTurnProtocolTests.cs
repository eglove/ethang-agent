namespace eThangAgent.Transport.ACL.Tests;

/// <summary>The root-turn wire codec's strict-at-both-boundaries contract (spec D6):
///     requests and events round-trip byte-exactly, a request without a turn id is a
///     named protocol error, and an event kind outside the four the session view
///     renders is refused rather than silently forwarded.</summary>
public class RootTurnProtocolTests
{
  [Fact]
  public void RootTurnRequest_SerializesAndRoundTrips()
  {
    RootTurnRequest request = new("turn-1", "hello", ["seed-a", "seed-b"]);

    string json = RootTurnProtocol.SerializeRequest(request);
    RootTurnRequest parsed = RootTurnProtocol.ParseRequest(json);

    Assert.Equal(request, parsed);
    Assert.Equal(["seed-a", "seed-b"], parsed.Seed);
  }

  [Fact]
  public void ParseRequest_MissingTurnId_Throws()
  {
    const string json = /*lang=json,strict*/ "{\"Input\":\"x\",\"Seed\":[]}";

    _ = Assert.Throws<InvalidOperationException>(() => RootTurnProtocol.ParseRequest(json));
  }

  [Fact]
  public void RootTurnEvent_SerializesAndRoundTrips()
  {
    RootTurnEvent evt = new("turn-1", "delta", "partial text");

    string json = RootTurnProtocol.SerializeEvent(evt);
    RootTurnEvent parsed = RootTurnProtocol.ParseEvent(json);

    Assert.Equal(evt, parsed);
  }

  [Fact]
  public void ParseEvent_UnknownKind_Throws()
  {
    const string json = /*lang=json,strict*/ "{\"TurnId\":\"t\",\"Kind\":\"bogus\",\"Payload\":\"\"}";

    _ = Assert.Throws<InvalidOperationException>(() => RootTurnProtocol.ParseEvent(json));
  }
}
