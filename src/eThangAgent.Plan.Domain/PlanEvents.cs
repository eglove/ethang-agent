namespace eThangAgent.PlanDomain;

/// <summary>Raised when a plan is created.</summary>
public record PlanCreated(int PlanId, string SessionId, DateTimeOffset At);

/// <summary>Raised when a plan's steps change (added, updated, removed).</summary>
public record PlanStepChanged(int PlanId, int Position, string Kind, DateTimeOffset At);

/// <summary>Raised when a plan's status changes.</summary>
public record PlanStatusChanged(int PlanId, PlanStatus From, PlanStatus To, DateTimeOffset At);
