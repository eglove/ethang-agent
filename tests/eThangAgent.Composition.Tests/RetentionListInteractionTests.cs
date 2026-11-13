using System.Text.Json;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition.Tests;

/// <summary>An enforced retention tier over an ORDERED list: the tier is one setting for the
///     whole list, it is enforced against the HEAD, and a backup never loosens it.
///     <para>THIS IS BEHAVIOUR 7 OF THE PLAN, OVER THE REAL PATH. The refusal is produced by the
///     OpenRouter ACL against the provider's own eligible list - not by a rule restated here -
///     and it is observed through a composed session, so what is pinned is that a session whose
///     head the tier cannot serve fails BEFORE any fallback is attempted.</para>
///     <para>THE HEAD IS THE ENTRY THE TIER IS ASKED ABOUT. A backup with no eligible endpoint
///     is the provider's refusal to make; the harness refuses only when the model that runs has
///     nowhere to go.</para>
///     <para>NOTHING HERE ASSERTS A DURATION: every wait is a ceiling, never a latency
///     contract.</para></summary>
[Trait("Category", "Process")]
public class RetentionListInteractionTests
{
  private const string Head = "alpha/model";
  private const string Second = "beta/other";

  /// <summary>The one endpoint both entries advertise, so the eligible list can name it: the
  ///      catalog keys an endpoint on (model, provider name, tag), and both payloads derive the
  ///      provider name from the tag's first segment.</summary>
  private const string EndpointTag = "acme/standard";

  // 7: the tier fails the turn naming itself, before any fallback is attempted.

  [Fact]
  public async Task AnEnforcedTierWithNoEligibleHeadEndpoint_FailsTheTurnNamingTheTier_BeforeAnyFallback()
  {
    using ComposedHostHarness session = await EligibleWorkspaceAsync();
    // ZDR is the strict tier, and the eligible list below names only the BACKUP's endpoint: the
    // head the session runs on has nowhere to go.
    session.Preference.Retention = RetentionPolicy.ZeroDataRetention;

    Result<string> turn = await session.SendAsync("hi").ConfigureAwait(true);

    Assert.False(turn.IsSuccess, "an unservable head must fail the turn under an enforced tier");
    Assert.Equal("RetentionUnavailable", turn.Error?.Code);
    // IT NAMES THE TIER, and it names the entry that could not be served: a user reading this
    // must know which setting refused the turn and which entry it refused.
    Assert.Contains("ZDR", turn.Error!.Message, StringComparison.Ordinal);
    Assert.Contains(Head, turn.Error.Message, StringComparison.Ordinal);
    // BEFORE ANY FALLBACK: the provider never saw a request. A backup is not an endpoint the
    // tier admitted, so sending the list would ask the provider to fall through a restriction
    // the harness exists to enforce.
    Assert.Equal(0, session.Provider.ResponseRequestCount);
  }

  [Fact]
  public async Task AnEnforcedTierTheHeadCanMeet_SendsTheWholeListUnderTheRestriction()
  {
    // The same tier and the same list with the HEAD admitted: the list goes out, the restriction
    // rides the body's provider block, and the turn is served.
    using ComposedHostHarness session = await EligibleWorkspaceAsync(headEligible: true);
    session.Preference.Retention = RetentionPolicy.ZeroDataRetention;

    Result<string> turn = await session.SendAsync("hi").ConfigureAwait(true);

    Assert.True(turn.IsSuccess, turn.Error?.Message);
    using JsonDocument body = JsonDocument.Parse(Assert.Single(session.Provider.ResponseBodies));
    Assert.True(body.RootElement.GetProperty("provider").GetProperty("zdr").GetBoolean(),
        "an enforced tier states itself on every request it allows");
    Assert.Equal(2, body.RootElement.GetProperty("models").GetArrayLength());
  }

  /// <summary>A session whose provider describes both entries' endpoints, plus an eligible
  ///      list that admits exactly one of them: the head when <paramref name="headEligible"/>,
  ///      the backup otherwise.
  ///      <para>THE ENDPOINT PAYLOADS ARE WHAT MAKE THE LIST CONSULTED. A model whose catalog rows
  ///      describe no endpoint proves nothing about eligibility, and the ACL correctly reads that
  ///      absence as 'not consulted' rather than as a refusal.</para></summary>
  private static async Task<ComposedHostHarness> EligibleWorkspaceAsync(bool headEligible = false)
  {
    ComposedHostHarness session = await ComposedHostHarness
        .StartAsync([Head, Second]).ConfigureAwait(false);
    _ = session.Provider
        .ServesEndpoints(Head, MockProviderServer.Endpoints(EndpointTag))
        .ServesEndpoints(Second, MockProviderServer.Endpoints(EndpointTag));
    session.Provider.ZeroDataRetentionBody = MockProviderServer.ZeroDataRetention(
        (headEligible ? Head : Second, EndpointTag));
    return session;
  }
}
