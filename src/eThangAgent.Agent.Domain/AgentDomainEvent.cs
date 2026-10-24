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

/// <summary>One provider call's token accounting, recorded as TELEMETRY (issue #270): the
///     provider-scored counts #262 surfaces, made durable so a measurement can be computed
///     from persisted data alone. Same identity as <see cref="AgentReasoningRecorded"/> —
///     (agent, turn, iteration) — so a turn's usage rows and reasoning rows line up.
///     <para>Both optional counts stay NULL when the provider did not report them, never
///     coerced to zero (the rule <c>TokenUsage</c> established): a provider reporting
///     <c>reasoning_tokens: 0</c> states a different fact from one reporting nothing, and a
///     fabricated zero would make the two indistinguishable in exactly the measurement this
///     row exists to serve.</para>
///     <para>Telemetry only, like its reasoning sibling: nothing reads this back into a
///     request, so the next request's bytes are unchanged by it. It carries no price —
///     per-call cost accounting belongs to the usage ledger (#21-#24).</para></summary>
public sealed record AgentUsageRecorded(AgentId AgentId, DateTimeOffset OccurredAt, Guid TurnId, int Iteration,
    int InputTokens, int OutputTokens, int? CachedInputTokens, int? ReasoningTokens)
    : AgentDomainEvent(AgentId, OccurredAt);
