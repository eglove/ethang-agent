namespace eThangAgent.ToolDomain;

/// <summary>Outcome of a successful index-only commit.</summary>
public sealed record GitCommitOutcome(string Hash, string Branch, string Message);
