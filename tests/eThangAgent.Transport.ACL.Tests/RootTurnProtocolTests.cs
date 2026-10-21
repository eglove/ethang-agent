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
  public void RootTurnRequest_NullSeed_ComparesAsEmpty_AndHashesConsistently()
  {
    // A peer that omits Seed deserializes to null; the codec's structural equality
    // treats null as empty, so such a request still equals its empty-seed twin and
    // both hash the same (a HashSet/Dictionary key must not disagree with Equals).
    RootTurnRequest nullSeed = new("turn-1", "hello", null!);
    RootTurnRequest emptySeed = new("turn-1", "hello", []);

    Assert.Equal(emptySeed, nullSeed);
    Assert.Equal(emptySeed.GetHashCode(), nullSeed.GetHashCode());
    Assert.NotEqual(nullSeed, nullSeed with { Input = "different" });
  }

  [Fact]
  public void ParseRequest_AbsentSeedField_YieldsAnEmptySeed()
  {
    // The wire shape a peer that omits the field produces: no Seed property at all.
    const string json = /*lang=json,strict*/ "{\"TurnId\":\"turn-1\",\"Input\":\"hello\"}";

    RootTurnRequest parsed = RootTurnProtocol.ParseRequest(json);

    Assert.Equal("turn-1", parsed.TurnId);
    Assert.Empty(parsed.Seed ?? []);
  }

  [Fact]
  public void ParseEvent_UnknownKind_Throws()
  {
    const string json = /*lang=json,strict*/ "{\"TurnId\":\"t\",\"Kind\":\"bogus\",\"Payload\":\"\"}";

    _ = Assert.Throws<InvalidOperationException>(() => RootTurnProtocol.ParseEvent(json));
  }
}
