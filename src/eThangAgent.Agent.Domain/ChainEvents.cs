namespace eThangAgent.AgentDomain;

/// <summary>Raised when a chain is created (issue #300). Carries the plan it executes, the
/// plan version it started at, and the authoring session so a surface can tell a chain a
/// model started from one the harness started.</summary>
public sealed record ChainCreated(int ChainId, int PlanId, int PlanVersion, string? SessionId, DateTimeOffset At);

/// <summary>Raised when a chain step is claimed or settled. <paramref name="Kind"/> is
/// "claimed" | "done" | "failed" | "skipped" | "parked".</summary>
public sealed record ChainStepChanged(int ChainId, int Position, string Kind, ChainActor Actor, DateTimeOffset At);

/// <summary>Raised when a chain's status changes, naming the actor on both sides so a
/// listener never has to guess who moved it.</summary>
public sealed record ChainStatusChanged(int ChainId, ChainStatus From, ChainStatus To, ChainActor Actor,
    string? ParkReason, DateTimeOffset At);
