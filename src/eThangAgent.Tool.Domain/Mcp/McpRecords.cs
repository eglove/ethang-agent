using System.Text.Json;

namespace eThangAgent.ToolDomain.Mcp;

/// <summary>One tool a connected MCP server advertises (issue #104): the server-local
///     name plus its description. Descriptions are untrusted server text carried
///     verbatim. The namespaced grant id (mcp.server.tool) is derived, not stored.</summary>
/// <param name="Name">The server-local tool name.</param>
/// <param name="Description">The server's description text, or null.</param>
public sealed record McpToolInfo(string Name, string? Description);

/// <summary>How a configured server's connection stands in the session pool.</summary>
public enum McpConnectionState
{
  /// <summary>Never connected this session (lazy connect: nothing has dispatched to it).</summary>
  NotConnected,

  /// <summary>Connected; the tool list is cached.</summary>
  Connected,

  /// <summary>The last connect attempt failed; the message explains why.</summary>
  Failed,
}

/// <summary>One configured server's current view: its row fields plus the pool's
///     connection state. Listing never connects anything (B5).</summary>
/// <param name="Name">The server's config name.</param>
/// <param name="Approval">The trust state from the config row.</param>
/// <param name="Transport">stdio or http.</param>
/// <param name="CommandOrUrl">The command (stdio) or URL (http) from the row.</param>
/// <param name="State">The pool's connection state.</param>
/// <param name="Tools">The cached tool list; empty unless connected.</param>
/// <param name="Error">The failure message when State is Failed; else null.</param>
// CA1054/CA1056: deliberately raw text - for stdio transports this is a command
// (not a URI at all); the same named deviation as McpServerConfig.
#pragma warning disable CA1054, CA1056
public sealed record McpServerStatus(
    string Name,
    McpApprovalState Approval,
    McpTransport Transport,
    string CommandOrUrl,
    McpConnectionState State,
    IReadOnlyList<McpToolInfo> Tools,
    string? Error);
#pragma warning restore CA1054, CA1056

/// <summary>Commands the dispatch tool sends the seam (the closed command set).
///     Validation happens in the tool's parser; these records are already valid.</summary>
// CA1034: the nested command/outcome cases ARE the seam's contract; public nested
// cases keep the case names as declared (the ComputerCommand named decision).
#pragma warning disable CA1034
public abstract record McpCommand
{
  /// <summary>Lists configured servers with connection state; connects nothing.</summary>
  public sealed record ListServers() : McpCommand;

  /// <summary>Calls one tool on one server with a JSON arguments object.</summary>
  /// <param name="Server">The configured server name.</param>
  /// <param name="Tool">The server-local tool name.</param>
  /// <param name="Arguments">The arguments object (empty object when omitted).</param>
  public sealed record CallTool(string Server, string Tool, JsonElement Arguments) : McpCommand;
}

/// <summary>Outcomes the seam returns (the closed outcome set). Failures carry
///     typed error codes; the tool renders them verbatim.</summary>
public abstract record McpOutcome
{
  /// <summary>The listing result.</summary>
  /// <param name="Servers">One status per configured server, ordered by name.</param>
  public sealed record Status(IReadOnlyList<McpServerStatus> Servers) : McpOutcome;

  /// <summary>A completed tool call.</summary>
  /// <param name="Content">The rendered text content the model receives.</param>
  /// <param name="IsError">True when the server reported the call itself failed
  ///     (CallToolResult.IsError) - the content is still the server's text.</param>
  public sealed record Called(string Content, bool IsError) : McpOutcome;

  /// <summary>A typed failure: unknown server/tool, unapproved server, connect
  ///     failure, call failure, or a storage fault.</summary>
  /// <param name="Code">The canonical error code (McpServerNotFound, McpToolNotFound,
  ///     McpServerNotApproved, McpConnectFailed, McpCallFailed, StorageUnavailable).</param>
  /// <param name="Message">The human-readable message.</param>
  public sealed record Failure(string Code, string Message) : McpOutcome;
}
#pragma warning restore CA1034
