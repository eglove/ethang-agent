namespace eThangAgent.AgentDomain;

/// <summary>Request to spawn a child agent. TaskPrompt is required; Model falls back to the
///     configured default; Label is free text for humans and logs. Priority orders the
///     concurrency-boundary queue (higher wakes first; default inherits the parent's flow,
///     0); Contract carries the persisted spawn agreement (grants, ceilings, urgency).</summary>
public sealed record SpawnRequest(string TaskPrompt, string? Model = null, string? Label = null,
    int Priority = 0, SpawnContract? Contract = null);
