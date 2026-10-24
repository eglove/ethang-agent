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
