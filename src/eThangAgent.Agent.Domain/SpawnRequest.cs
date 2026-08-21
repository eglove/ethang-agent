namespace eThangAgent.AgentDomain;

/// <summary>Request to spawn a child agent. TaskPrompt is required; Model falls back to the configured default; Label is free text for humans and logs.</summary>
public sealed record SpawnRequest(string TaskPrompt, string? Model = null, string? Label = null);