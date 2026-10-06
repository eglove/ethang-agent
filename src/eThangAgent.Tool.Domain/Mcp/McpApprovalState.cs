namespace eThangAgent.ToolDomain.Mcp;

/// <summary>The trust state of a configured MCP server (the install-is-a-trust-event
///     contract): a pending server never connects; approval is the human trust event;
///     revocation withdraws it. Persisted from day one (V15) so the trust flow never
///     needs a schema change.</summary>
public enum McpApprovalState
{
  /// <summary>Configured but not trusted yet - never connects.</summary>
  Pending,

  /// <summary>Trusted by the user - eligible to connect on first dispatch.</summary>
  Approved,

  /// <summary>Trust withdrawn - stays configured, never connects.</summary>
  Revoked,
}
