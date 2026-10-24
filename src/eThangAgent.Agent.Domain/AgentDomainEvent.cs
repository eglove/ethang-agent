using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

public abstract record AgentDomainEvent(AgentId AgentId, DateTimeOffset OccurredAt);

public sealed record AgentSpawned(AgentId AgentId, DateTimeOffset OccurredAt, int Depth, string ModelUsed, string? Label)
    : AgentDomainEvent(AgentId, OccurredAt);

public sealed record AgentCompleted(AgentId AgentId, DateTimeOffset OccurredAt, AgentStatus Status, AgentFailureReason? Reason)
    : AgentDomainEvent(AgentId, OccurredAt);

/// <summary>One iteration's reasoning text, recorded as TELEMETRY (issue #263): what the
///     provider streamed as hidden reasoning for one provider call.
///     <para>This is the third channel of the parent issue's three: it is stored for
///     debugging, evals, and safety review — never for context. Nothing reads this event
///     back into a request (the request builder has no path to the event log at all), and
///     nothing renders it as display (the display channel is the stream callback). Its
///     cost is storage, not tokens.</para>
///     <para>Identity is (agent, turn, iteration): <see cref="TurnId"/> is minted per turn
///     so a resumed conversation's traces never collide with an earlier turn's, and
///     <see cref="Iteration"/> is the 0-based provider call within that turn.</para></summary>
public sealed record AgentReasoningRecorded(AgentId AgentId, DateTimeOffset OccurredAt, Guid TurnId, int Iteration,
    string Text) : AgentDomainEvent(AgentId, OccurredAt);

/// <summary>One provider call's token accounting and cost, recorded as TELEMETRY (issues
///     #270, #22): the provider-scored counts #262 surfaces plus the price, call class,
///     and attribution the usage ledger needs, made durable so a measurement can be
///     computed from persisted data alone. Same identity as
///     <see cref="AgentReasoningRecorded"/> — (agent, turn, iteration) — so a turn's usage
///     rows and reasoning rows line up.
///     <para>Both optional counts stay NULL when the provider did not report them, never
///     coerced to zero (the rule <c>TokenUsage</c> established): a provider reporting
///     <c>reasoning_tokens: 0</c> states a different fact from one reporting nothing, and a
///     fabricated zero would make the two indistinguishable in exactly the measurement this
///     row exists to serve.</para>
///     <para>PRICE IS STAMPED AT WRITE TIME and that is the only reason this record had to
///     change: the catalog cache keeps one current price row per provider and no history,
///     so a call not priced when it happens is unpriced forever — a read-time join would
///     silently re-price history. A null price means the price was unknown (a routing
///     pseudo-model, a local provider, a catalog miss): the row contributes tokens but no
///     cost, and cost reads as null rather than being fabricated.</para>
///     <para>Every member added by #22 is nullable, so a payload written before they
///     existed deserializes into this record with them null — legacy rows stay valid, and
///     <see cref="EffectiveCallClass"/> gives the null class its one correct meaning.</para>
///     <para>Telemetry only, like its reasoning sibling: nothing reads this back into a
///     request, so the next request's bytes are unchanged by it.</para></summary>
public sealed record AgentUsageRecorded(AgentId AgentId, DateTimeOffset OccurredAt, Guid TurnId, int Iteration,
    int InputTokens, int OutputTokens, int? CachedInputTokens, int? ReasoningTokens,
    UsageCallClass? CallClass = null, string? WorkspaceId = null, string? Provider = null,
    decimal? PromptPricePerToken = null, decimal? PromptCacheReadPricePerToken = null,
    decimal? CompletionPricePerToken = null)
    : AgentDomainEvent(AgentId, OccurredAt)
{
  /// <summary>The class this row belongs to, with the legacy rule applied: a null class
  ///     reads as <see cref="UsageCallClass.MainLoop"/>, because every row written before
  ///     the class existed came from the loop.</summary>
  public UsageCallClass EffectiveCallClass => CallClass ?? UsageCallClass.MainLoop;

  /// <summary>Strictly validated construction (issue #23, B10): a record the loop cannot
  ///     honestly write is a DOMAIN ERROR here, never a coerced row. The recording site
  ///     counts a rejected record as a drop and surfaces it once per turn, so a bad record
  ///     is never silent — and never becomes a corrupt measurement.
  ///     <para>Negative token counts are rejected because a provider cannot report them and
  ///     a fabricated negative would poison every rollup it enters. The positional
  ///     constructor stays for deserialization (a stored payload is read back as written)
  ///     and for tests; production records go through this factory.</para></summary>
  public static Result<AgentUsageRecorded> Create(AgentId agentId, DateTimeOffset occurredAt, Guid turnId,
      int iteration, TokenUsage usage, UsageAttribution attribution)
  {
    ArgumentNullException.ThrowIfNull(attribution);
    return NegativeCount(usage) is { } offending
        ? Result.Failure<AgentUsageRecorded>(new DomainError("InvalidUsageCounts",
            $"Usage count '{offending}' is negative; a provider cannot report a negative token count."))
        : Result.Success(new AgentUsageRecorded(agentId, occurredAt, turnId, iteration,
            usage.InputTokens, usage.OutputTokens, usage.CachedInputTokens, usage.ReasoningTokens,
            attribution.CallClass, attribution.WorkspaceId, attribution.Provider,
            attribution.Prices.PromptPricePerToken, attribution.Prices.PromptCacheReadPricePerToken,
            attribution.Prices.CompletionPricePerToken));
  }

  /// <summary>The name of the first negative count, or null when every count is sane.</summary>
  private static string? NegativeCount(TokenUsage usage) =>
      Counts(usage).FirstOrDefault(count => count.Value < 0).Key;

  /// <summary>The four counts as named pairs, in the order they are checked.</summary>
  private static IEnumerable<KeyValuePair<string, int>> Counts(TokenUsage usage)
  {
    yield return new(nameof(usage.InputTokens), usage.InputTokens);
    yield return new(nameof(usage.OutputTokens), usage.OutputTokens);
    yield return new(nameof(usage.CachedInputTokens), usage.CachedInputTokens ?? 0);
    yield return new(nameof(usage.ReasoningTokens), usage.ReasoningTokens ?? 0);
  }
}

/// <summary>Who and what a usage row attributes to, and on what price basis (issue #23):
///     the call class, the workspace, the provider whose price applied, and the three
///     prices resolved at write time. Grouped because they always travel together and the
///     record factory would otherwise carry a parameter per column.</summary>
/// <param name="CallClass">The class of the call: main loop or compaction.</param>
/// <param name="WorkspaceId">The workspace the work belongs to; null when unwired.</param>
/// <param name="Provider">The provider whose price was stamped; null when unnamed.</param>
/// <param name="Prices">The three per-token prices, each null when unknown.</param>
public sealed record UsageAttribution(UsageCallClass CallClass, string? WorkspaceId, string? Provider,
    UsagePrices Prices)
{
  /// <summary>The attribution of a call whose workspace, provider, and prices are all
  ///     unknown — the legacy shape, where tokens are recorded and cost reads as null.</summary>
  public static UsageAttribution Unattributed(UsageCallClass callClass) =>
      new(callClass, null, null, UsagePrices.Unknown);
}
