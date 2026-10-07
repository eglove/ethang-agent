namespace eThangAgent.ToolDomain.Mcp;

/// <summary>One logged trust or gate decision (issues #107/#109): an append-only row
///     naming the server, the decision, and the human or policy reason. The log is a
///     record of decisions, never a state source (P2) - the config row's columns
///     remain the truth.</summary>
/// <param name="ServerId">The server row the decision is about.</param>
/// <param name="Decision">The decision kind (approved, revoked, gate-denied).</param>
/// <param name="Detail">The human-readable reason, when recorded.</param>
/// <param name="CreatedAt">When the decision landed.</param>
public sealed record McpDecision(int ServerId, string Decision, string? Detail, DateTimeOffset CreatedAt);
