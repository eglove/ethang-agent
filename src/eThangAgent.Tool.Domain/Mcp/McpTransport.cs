namespace eThangAgent.ToolDomain.Mcp;

/// <summary>How an MCP server is reached: a spawned stdio child process (command plus
///     arguments) or a Streamable HTTP endpoint (URL plus headers).</summary>
public enum McpTransport
{
  /// <summary>Spawned child process speaking MCP over stdin/stdout.</summary>
  Stdio,

  /// <summary>Remote endpoint speaking MCP over Streamable HTTP.</summary>
  Http,
}
