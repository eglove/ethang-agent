namespace eThangAgent.ToolDomain.Mcp;

/// <summary>The per-server call gate (issue #109): which calls on this server wait for
///     a human decision. None is the default - every dispatch executes (the approval
///     STATE machine is the trust boundary; the gate is the per-call one). Mutating
///     gates tools the server does not declare read-only: an undeclared or
///     destructive tool is mutating by default (the MCP spec has no mutation flag,
///     so classification is policy, not metadata).</summary>
public enum McpGateMode
{
  /// <summary>No per-call gate: approved servers dispatch freely.</summary>
  None,

  /// <summary>Mutating calls are gated: the harness refuses them with a structured,
  ///     logged refusal until the user lifts the gate (the dialog toggle is the
  ///     human decision path; the harness has no mid-turn approval surface).</summary>
  Mutating,
}
