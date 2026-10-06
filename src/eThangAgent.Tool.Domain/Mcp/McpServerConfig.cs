namespace eThangAgent.ToolDomain.Mcp;

/// <summary>One configured MCP server row (issue #103): the scope split rides
///     WorkspaceId (null = global, a workspace id = that workspace only); args/env/headers
///     are the config form's JSON shapes kept as text so the schema stays stable across
///     transport differences; approval state and pinned version are columns from day one
///     so the trust flow needs no migration.</summary>
/// <param name="Id">Store-assigned row id.</param>
/// <param name="Name">The server name - unique within its scope.</param>
/// <param name="Transport">How the server is reached.</param>
/// <param name="CommandOrUrl">stdio command, or the HTTP endpoint URL.</param>
/// <param name="ArgsJson">Launch arguments as a JSON array (stdio) - '[]' for HTTP.</param>
/// <param name="EnvJson">Server environment entries as a JSON object - '{}' when none.</param>
/// <param name="HeadersJson">Static HTTP headers as a JSON object - '{}' for stdio.</param>
/// <param name="WorkspaceId">Null = the global scope; a workspace id = that workspace only.</param>
/// <param name="ApprovalState">The trust state; pending servers never connect.</param>
/// <param name="PinnedVersion">The pinned install version, if any; never auto-updated.</param>
/// <param name="CreatedAt">When the row was created.</param>
// CA1054/CA1056: deliberately raw text — for stdio transports this is a command
// (not a URI at all), and validation is the config door's job, not the record's.
#pragma warning disable CA1054, CA1056
public sealed record McpServerConfig(
    int Id,
    string Name,
    McpTransport Transport,
    string CommandOrUrl,
    string ArgsJson,
    string EnvJson,
    string HeadersJson,
    string? WorkspaceId,
    McpApprovalState ApprovalState,
    string? PinnedVersion,
    DateTimeOffset CreatedAt);
#pragma warning restore CA1054, CA1056
