using System.Globalization;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Mcp;

/// <summary>The 'mcp' dispatch tool (issue #104): the model's single door to configured
///     MCP servers. One tool on the loop registry keeps the definition budget flat no
///     matter how many servers are configured - servers and their tools are discovered
///     through the listing action, never upfront definitions (B1). Input is parsed
///     strictly by <see cref="McpToolInput"/>, dispatched to the
///     <see cref="IMcpServerAccess"/> seam, and rendered on the fixed output contract.
///     The seam never throws domain errors - failures arrive as
///     <see cref="McpOutcome.Failure"/> values and are rendered, not caught.</summary>
public sealed class McpTool(IMcpServerAccess access, IMcpGrantScope? grants = null) : ITool
{
  private readonly IMcpServerAccess _access = access ?? throw new ArgumentNullException(nameof(access));
  private readonly IMcpGrantScope? _grants = grants;

  /// <summary>The wire name of every admitted action, in advertisement order.</summary>
  private static readonly string[] ActionNames = ["list", "call"];

  /// <inheritdoc />
  public ToolDefinition Definition { get; } = new(
      "mcp",
      BuildDescription(),
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber,
              ToolTimeout.ParameterDescription, Minimum: 1),
          new ToolParameter("action", ToolParameterType.Text,
              "Exactly one of list, call (case-sensitive)."),
          new ToolParameter("server", ToolParameterType.Text,
              "Required for call: the configured server name (see list)."),
          new ToolParameter("tool", ToolParameterType.Text,
              "Required for call: the server-local tool name (see list)."),
          new ToolParameter("arguments", ToolParameterType.Text,
              "Optional for call: a JSON object of tool arguments (default {})."),
      ],
      [ToolTimeout.ParameterName, "action"]);

  /// <summary>The description IS the contract: every action, key, output annotation,
  ///     error code, and the lazy-connect promise appears verbatim so the model never
  ///     guesses what it is looking at.</summary>
  private static string BuildDescription()
  {
    return "Call tools on configured MCP (Model Context Protocol) servers through one dispatch surface."
        + " timeoutSeconds is mandatory. action is exactly one of " + string.Join(", ", ActionNames)
        + " (case-sensitive)."
        + " list takes no other keys and reports every configured server with its approval state, transport,"
        + " connection state, and - for connected servers - the cached tool list; it never connects anything."
        + " call requires server and tool and takes an optional arguments object (default {});"
        + " the first call to a server connects it lazily and caches its tool list; later calls reuse the session."
        + " Servers connect only when approved; a pending or revoked server refuses with McpServerNotApproved."
        + " Output: list renders one annotation line '[mcp] N server(s) configured' then one line per server -"
        + " 'name | approval | transport | state' where state is 'not connected', 'connected, N tool(s): a, b',"
        + " or 'error: <message>'. call renders the server's text content verbatim; when the server marks the"
        + " call failed the result is still delivered but flagged as an error."
        + " Failures render 'Error [Code]: <message>'."
        + " Error codes: McpServerNotFound (unknown server name - run list), McpToolNotFound (unknown tool on a"
        + " connected server - run list), McpServerNotApproved (the server is pending or revoked - the user must"
        + " approve it), McpConnectFailed (the server process or endpoint failed - the status entry appears in"
        + " list), McpCallFailed (the call could not complete), StorageUnavailable (the server config store"
        + " could not be read). Errors are safe to retry with corrected input.";
  }

  /// <inheritdoc />
  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<McpToolInput> parsed = McpToolInput.Create(input.JsonArguments);
    if (!parsed.IsSuccess)
    {
      return Task.FromResult(Err(parsed.Error));
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    return !budget.IsSuccess
      ? Task.FromResult(Err(budget.Error))
      : ToolExecution.RunAsync(input.Name, budget.Value.Timeout, token => RunAsync(parsed.Value, token), ct);
  }

  private async Task<ToolResult> RunAsync(McpToolInput args, CancellationToken ct)
  {
    // Dispatch-time grant binding (issue #108): the filtered registry sees the
    // dispatch tool itself; the RESOLVED id (mcp.server.tool) is re-checked against
    // the ambient grant scope before anything dispatches. No scope wired = the
    // full-reach default (root agents, ungranted children, legacy wiring).
    if (args.Action == McpAction.Call && _grants is { } grantScope)
    {
      string resolvedId = $"mcp.{args.Server}.{args.Tool}";
      string? refusal = grantScope.RefusalFor(resolvedId);
      if (refusal is not null)
      {
        return new ToolResult(refusal, true);
      }
    }

    McpCommand command = args.Action switch
    {
      McpAction.List => new McpCommand.ListServers(),
      McpAction.Call => new McpCommand.CallTool(args.Server!, args.Tool!, args.Arguments),
      _ => throw new InvalidOperationException("unreachable: action is a validated enum"),
    };
    McpOutcome outcome = await _access.ExecuteAsync(command, ct).ConfigureAwait(false);
    return outcome switch
    {
      McpOutcome.Status status => RenderStatus(status, args.Action == McpAction.List ? _grants : null),
      McpOutcome.Called call => new ToolResult(call.Content, call.IsError),
      McpOutcome.Failure failure => new ToolResult(
          $"Error [{failure.Code}]: {failure.Message}", true),
      _ => throw new InvalidOperationException("unreachable: outcome is a validated record family"),
    };
  }

  private static ToolResult RenderStatus(McpOutcome.Status status, IMcpGrantScope? grants)
  {
    // A scoped agent's listing shows only servers its grants can reach (issue #108):
    // the config's server names are not leaked past the grant boundary.
    IEnumerable<McpServerStatus> visible = grants is null
        ? status.Servers
        : status.Servers.Where(s => grants.Reachable(s.Name));
    List<McpServerStatus> servers = [.. visible];
    if (servers.Count == 0)
    {
      return new ToolResult("[mcp] 0 server(s) configured", false);
    }

    string[] lines = new string[servers.Count + 1];
    lines[0] = string.Create(CultureInfo.InvariantCulture,
        $"[mcp] {servers.Count} server(s) configured");
    for (int i = 0; i < servers.Count; i++)
    {
      lines[i + 1] = RenderServer(servers[i]);
    }

    return new ToolResult(string.Join("\n", lines), false);
  }

  private static string RenderServer(McpServerStatus server)
  {
    string state = server.State switch
    {
      McpConnectionState.NotConnected => "not connected",
      McpConnectionState.Connected => string.Create(CultureInfo.InvariantCulture,
          $"connected, {server.Tools.Count} tool(s): {string.Join(", ", server.Tools.Select(t => t.Name))}"),
      McpConnectionState.Failed => $"error: {server.Error}",
      _ => throw new InvalidOperationException("unreachable: state is a validated enum"),
    };
    return $"{server.Name} | {ApprovalText(server.Approval)} | {TransportText(server.Transport)} | {state}";
  }

  private static string ApprovalText(McpApprovalState state) => state switch
  {
    McpApprovalState.Approved => "approved",
    McpApprovalState.Pending => "pending",
    McpApprovalState.Revoked => "revoked",
    _ => "revoked",
  };

  private static string TransportText(McpTransport transport) =>
      transport == McpTransport.Stdio ? "stdio" : "http";

  private static ToolResult Err(DomainError error) => new(
      $"Error [{error.Code}]: {error.Message}", true);
}
