namespace eThangAgent.ToolDomain.Mcp;

/// <summary>One connected MCP server's client session (issue #104): the Tool Domain's
///     transport-neutral view over the SDK's McpClient. The ACL adapts the SDK;
///     the domain never learns transports exist. Implementations dispose their
///     underlying session when disposed.</summary>
public interface IMcpClientSession : IAsyncDisposable
{
  /// <summary>Whether the session's underlying transport is dead (a stdio server
  ///     process that exited). The pool polls this before reusing a session and
  ///     reconnects when it reports true; a transport without a process (in-memory,
  ///     HTTP) never dies, so the default is false.</summary>
  bool HasExited => false;

  /// <summary>Lists the server's tools. The result is the ACL's parsed view of the
  ///     server's tool metadata (names plus descriptions).</summary>
  Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct = default);

  /// <summary>Calls one tool with a JSON arguments object; the JSON is forwarded
  ///     verbatim. The result carries the server's rendered text content and its
  ///     own error flag (CallToolResult.IsError).</summary>
  Task<McpToolCallResult> CallToolAsync(string tool, string argumentsJson, CancellationToken ct = default);
}

/// <summary>The outcome of one tool call: the server's text content plus the
///     server's own error flag.</summary>
/// <param name="Content">The rendered text content (all text blocks joined).</param>
/// <param name="IsError">The server's CallToolResult.IsError flag.</param>
public sealed record McpToolCallResult(string Content, bool IsError);
