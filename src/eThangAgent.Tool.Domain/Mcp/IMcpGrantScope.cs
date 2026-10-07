namespace eThangAgent.ToolDomain.Mcp;

/// <summary>The ambient MCP grant scope (issue #108): the effective grant entries the
///     CURRENT agent run was resolved with, consulted by the dispatch tool at
///     DISPATCH time — the filtered registry sees the dispatch tool (mcp), so the
///     per-target check happens here, over the resolved id (mcp.server.tool).
///     Implemented with AsyncLocal by the composition (the workspace anchor scope's
///     pattern): the value flows down a child run's async flow, never sideways, and
///     an unset scope means the full-reach default (root agents, ungranted children,
///     legacy wiring) — byte-identical legacy behavior.</summary>
public interface IMcpGrantScope
{
  /// <summary>The structured refusal for a resolved dispatch id outside the ambient
  ///     grant set, or null when the id is granted. The refusal renders verbatim
  ///     (the GrantViolation contract — POLICY, never mistaken for TYPO).</summary>
  string? RefusalFor(string resolvedId);

  /// <summary>Whether a configured server is reachable under the ambient grant set —
  ///     the listing filter: a scoped agent's list shows only servers it could
  ///     dispatch to, never the full config (no server-name leak).</summary>
  bool Reachable(string serverName);
}
