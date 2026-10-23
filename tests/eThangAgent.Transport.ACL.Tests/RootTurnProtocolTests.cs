using eThangAgent.AgentDomain;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>The root-turn wire codec's strict-at-both-boundaries contract (spec D6):
///     requests and events round-trip byte-exactly, a request without a turn id is a
///     named protocol error, and an event kind outside the six the wire carries is
///     refused rather than silently forwarded.</summary>
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
  public void RootTurnRequest_Generation_RoundTrips()
  {
    // RULING 2: the generation is the per-turn identity that lets the client ignore a
    // stale settle for a REUSED turn id. It must survive the wire.
    RootTurnRequest request = new("turn-1", "hello", [], Generation: 7);

    RootTurnRequest parsed = RootTurnProtocol.ParseRequest(RootTurnProtocol.SerializeRequest(request));

    Assert.Equal(7, parsed.Generation);
    Assert.Equal(request, parsed);
  }

  [Fact]
  public void RootTurnEvent_Generation_RoundTrips()
  {
    RootTurnEvent evt = new("turn-1", "delta", "chunk", Generation: 7);

    RootTurnEvent parsed = RootTurnProtocol.ParseEvent(RootTurnProtocol.SerializeEvent(evt));

    Assert.Equal(7, parsed.Generation);
    Assert.Equal(evt, parsed);
  }

  [Fact]
  public void RootTurnRequest_DifferingGeneration_IsNotEqual()
  {
    // The generation is part of identity: two requests for the same turn id from different
    // generations must never compare equal, or a stale one could be mistaken for a live one.
    RootTurnRequest first = new("turn-1", "hello", [], Generation: 1);
    RootTurnRequest second = new("turn-1", "hello", [], Generation: 2);

    Assert.NotEqual(first, second);
  }

  [Fact]
  public void RootTurnBudget_RejectsNonPositiveSendBound()
  {
    // The send bound is the second of TWO bounds left after issue #248 removed the
    // total-duration ceiling: a zero send bound would cancel every frame before its header
    // could be written, so it is validated exactly like the idle bound — on construction
    // and through the init accessor a 'with' expression assigns.
    _ = Assert.Throws<ArgumentOutOfRangeException>(
        () => new RootTurnBudget(TimeSpan.FromMinutes(1), TimeSpan.Zero));
    _ = Assert.Throws<ArgumentOutOfRangeException>(
        () => new RootTurnBudget(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(-1)));

    RootTurnBudget valid = new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));
    _ = Assert.Throws<ArgumentOutOfRangeException>(
        () => valid with { Send = TimeSpan.Zero });
  }

  [Fact]
  public void RootTurnBudget_SendBound_IsSeparateFromIdle()
  {
    // Finding 5: the send bound must NOT share the idle value. A lapsed write is cancelled,
    // and a cancellation that interrupts a frame costs the whole connection — so judging a
    // large frame by the SILENCE window would drop a slow-but-healthy host's connection.
    RootTurnBudget budget = new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5));

    Assert.Equal(TimeSpan.FromSeconds(30), budget.Idle);
    Assert.Equal(TimeSpan.FromMinutes(5), budget.Send);
    Assert.NotEqual(budget.Idle, budget.Send);
    // The production default must leave the send bound generous for a 4MB frame and tighter
    // than the retired one-hour ceiling that made it effectively unbounded.
    Assert.True(RootTurnBudget.Default.Send > RootTurnBudget.Default.Idle,
        "the default send bound must be more generous than the idle window");
    Assert.True(RootTurnBudget.Default.Send < TimeSpan.FromHours(1),
        "the default send bound must be minutes, not effectively unbounded");
  }

  [Fact]
  public void RootTurnBudget_HasNoTotalDurationBound()
  {
    // Issue #248: the wall-clock ceiling and its TurnCeilingExceeded failure are GONE. The
    // budget exposes exactly the two bounds that remain, so code that tried to read a
    // ceiling — or an error path that tried to raise one — cannot compile.
    string[] members = [.. typeof(RootTurnBudget).GetProperties().Select(p => p.Name).Order()];

    Assert.Equal(["Idle", "Send"], members);
  }

  [Fact]
  public void RootTurnBudget_RejectsNonPositiveBounds()
  {
    // Strict boundary (finding 3): a zero idle makes EVERY turn instantly unaddressable and
    // a non-positive send bound would cancel every frame. Now that the value is nominally
    // production configuration it must be validated, not silently accepted.
    _ = Assert.Throws<ArgumentOutOfRangeException>(
        () => new RootTurnBudget(TimeSpan.Zero, TimeSpan.FromMinutes(5)));
    _ = Assert.Throws<ArgumentOutOfRangeException>(
        () => new RootTurnBudget(TimeSpan.FromMinutes(1), TimeSpan.Zero));
    _ = Assert.Throws<ArgumentOutOfRangeException>(
        () => new RootTurnBudget(TimeSpan.FromSeconds(-1), TimeSpan.FromMinutes(5)));
    _ = Assert.Throws<ArgumentOutOfRangeException>(
        () => new RootTurnBudget(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(-1)));
  }

  [Fact]
  public void RootTurnBudget_WithExpression_CannotBypassValidation()
  {
    // Finding 3: validation in a property INITIALIZER runs only in the constructor, so a
    // 'with' expression could smuggle in a zero idle. The guard must hold on every path.
    RootTurnBudget valid = new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5));

    _ = Assert.Throws<ArgumentOutOfRangeException>(
        () => valid with { Idle = TimeSpan.Zero });
    _ = Assert.Throws<ArgumentOutOfRangeException>(
        () => valid with { Idle = TimeSpan.FromSeconds(-1) });
  }

  [Fact]
  public void RootTurnBudget_AcceptsPositiveBounds()
  {
    RootTurnBudget budget = new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5));

    Assert.Equal(TimeSpan.FromSeconds(30), budget.Idle);
    Assert.Equal(TimeSpan.FromMinutes(5), budget.Send);
  }

  [Fact]
  public void ParseEvent_Reasoning_IsAKnownKind()
  {
    // Issue #245: the model's streaming thoughts travel as their own wire kind. A host
    // that emits one must reach an app that recognizes it - a peer that refused it would
    // fault the connection, not the turn.
    const string json = /*lang=json,strict*/ "{\"TurnId\":\"t\",\"Kind\":\"reasoning\",\"Payload\":\"thinking\"}";

    RootTurnEvent parsed = RootTurnProtocol.ParseEvent(json);

    Assert.Equal("reasoning", parsed.Kind);
    Assert.Equal("thinking", parsed.Payload);
  }

  [Fact]
  public void ParseEvent_UnknownKind_Throws()
  {
    const string json = /*lang=json,strict*/ "{\"TurnId\":\"t\",\"Kind\":\"bogus\",\"Payload\":\"\"}";

    _ = Assert.Throws<InvalidOperationException>(() => RootTurnProtocol.ParseEvent(json));
  }

  [Fact]
  public void ParseEvent_TheLivenessHeartbeat_IsAKnownKind()
  {
    // The heartbeat is a WIRE kind, not a forward-compatible unknown: a host that sent one
    // to a client that refused it would fault the connection, not the turn.
    const string json = /*lang=json,strict*/ "{\"TurnId\":\"t\",\"Kind\":\"progress\",\"Payload\":\"alive\"}";

    RootTurnEvent parsed = RootTurnProtocol.ParseEvent(json);

    Assert.Equal("progress", parsed.Kind);
  }

  [Fact]
  public void ParseEvent_TheSessionRequest_IsAKnownKind()
  {
    // The session request (issue #234) is a WIRE kind, not a forward-compatible unknown:
    // a host that emits one mid-turn (the new_session tool firing) must reach an app that
    // recognizes it - a peer that refused it would fault the connection, not the turn.
    const string json = /*lang=json,strict*/ "{\"TurnId\":\"t\",\"Kind\":\"session-request\",\"Payload\":\"request\"}";

    RootTurnEvent parsed = RootTurnProtocol.ParseEvent(json);

    Assert.Equal("session-request", parsed.Kind);
  }

  [Fact]
  public void RootTurnRequest_TheIdleWindow_RoundTrips()
  {
    // The window the app will enforce rides the request, so the host can heartbeat inside
    // it. A lost window means the host guesses - and a guess that is too slow puts the
    // false failure back.
    RootTurnRequest request = new("turn-1", "hello", []) { IdleTimeout = TimeSpan.FromMinutes(2) };

    RootTurnRequest parsed = RootTurnProtocol.ParseRequest(RootTurnProtocol.SerializeRequest(request));

    Assert.Equal(TimeSpan.FromMinutes(2), parsed.IdleTimeout);
    Assert.Equal(request, parsed);
  }

  [Fact]
  public void RootTurnRequest_TheModelPreferencesCarrier_RoundTrips()
  {
    // The live model choice travels opaquely; the transport never interprets it.
    // The carrier is an opaque JSON document: the transport never interprets it, so it
    // is written as a raw literal rather than an escaped one.
    // JSON002 fires only in the format/IDE host; the pragma pair is the repo's named
    // decision for hand-written JSON test shapes.
#pragma warning disable JSON002 // Probable JSON string detected
    string carrier =
        """
        {"ModelId":"minimax/minimax-m3.1-flash:free"}
        """;
#pragma warning restore JSON002 // Probable JSON string detected
    RootTurnRequest request = new("turn-1", "hello", []) { ModelPreferences = carrier };

    RootTurnRequest parsed = RootTurnProtocol.ParseRequest(RootTurnProtocol.SerializeRequest(request));

    Assert.Equal(carrier, parsed.ModelPreferences);
    Assert.Equal(request, parsed);
  }
}
