using eThangAgent.ModelDomain;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>The <c>usage</c> wire kind (issue #24): one recorded provider call's spend
///     travels from the host process to the app, so the status bar's session cost advances
///     for a host-run turn. The ledger row is written in the host, so the in-process
///     callback path cannot carry it.
///
///     Anti-tautology: the payload's writer and reader are the PRODUCTION ones
///     (<see cref="RootTurnUsagePayload.Serialize"/> / <see cref="RootTurnUsagePayload.Parse"/>)
///     and the event goes through the real <see cref="RootTurnProtocol"/> codec, so a shape
///     that drifted between the two sides fails here rather than in production.</summary>
public class RootTurnUsagePayloadTests
{
  private static UsageRow PricedRow() => new("compaction", "test/model", "openrouter", "ws-1",
      1000, 100, 900, 42, 0.000002m, 0.0000002m, 0.000004m);

  [Fact]
  public void Usage_IsAKnownEventKind()
  {
    // The kind is added to the STRICT set, not smuggled through `progress`: a host that
    // emits one must reach an app that recognizes it.
    Assert.Contains("usage", RootTurnProtocol.EventKinds);
    RootTurnEvent evt = new("t", "usage", "{}");

    RootTurnEvent parsed = RootTurnProtocol.ParseEvent(RootTurnProtocol.SerializeEvent(evt));

    Assert.Equal("usage", parsed.Kind);
  }

  [Fact]
  public void ParseEvent_AnUnknownKindStillThrows()
  {
    // Adding a kind must not loosen the boundary: a peer that invents one is a protocol
    // fault, never a forward-compatible unknown.
    const string json = /*lang=json,strict*/ "{\"TurnId\":\"t\",\"Kind\":\"usage-detail\",\"Payload\":\"\"}";

    _ = Assert.Throws<InvalidOperationException>(() => RootTurnProtocol.ParseEvent(json));
  }

  [Fact]
  public void Serialize_RoundTripsEveryField()
  {
    UsageRow row = PricedRow();

    RootTurnUsagePayload parsed = Assert.IsType<RootTurnUsagePayload>(
        RootTurnUsagePayload.Parse(RootTurnUsagePayload.Serialize(row)));

    Assert.Equal(row, parsed.ToRow());
  }

  [Fact]
  public void Serialize_NullPricesStayNull()
  {
    // A null price means the price was unknown at write time. It must survive the wire as
    // null so the app counts the call as unpriced rather than as free.
    UsageRow row = new("main_loop", "m", null, null, 10, 1, null, null, null, null, null);

    RootTurnUsagePayload parsed = Assert.IsType<RootTurnUsagePayload>(
        RootTurnUsagePayload.Parse(RootTurnUsagePayload.Serialize(row)));

    Assert.Null(parsed.PromptPricePerToken);
    Assert.Null(parsed.PromptCacheReadPricePerToken);
    Assert.Null(parsed.CompletionPricePerToken);
    Assert.Null(UsageCost.Of(parsed.ToRow()));
  }

  [Fact]
  public void Parse_AnOlderHostsPayload_ReadsTheOmissionAsUnreported()
  {
    // A host of an older build emits no members at all. The app must read that as
    // 'nothing reported', never as a priced call of zero.
    RootTurnUsagePayload? parsed = RootTurnUsagePayload.Parse("{}");

    Assert.NotNull(parsed);
    Assert.Equal(0, parsed.InputTokens);
    Assert.Null(parsed.CachedInputTokens);
    // The legacy null-class rule applies on the wire too.
    Assert.Equal("main_loop", parsed.ToRow().CallClass);
  }

  [Fact]
  public void Parse_UnreadablePayload_IsNull()
      => Assert.Null(RootTurnUsagePayload.Parse("not json"));
}
