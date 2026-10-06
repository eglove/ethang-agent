namespace eThangAgent.ToolDomain.Mcp;

/// <summary>The MCP seam (issue #104): the Tool Domain's only door to configured MCP
///     servers. The ACL implements it over the official SDK's client sessions - lazy
///     connect, per-workspace pooling, cached discovery, and the approval gate. The
///     domain never learns transports, processes, or wire formats exist. Expected
///     failures arrive as <see cref="McpOutcome.Failure"/> values, never exceptions.</summary>
public interface IMcpServerAccess
{
  /// <summary>Executes one validated command. Listing never connects; calling connects
  ///     lazily on first use and reuses the pooled session after.</summary>
  Task<McpOutcome> ExecuteAsync(McpCommand command, CancellationToken ct = default);
}
