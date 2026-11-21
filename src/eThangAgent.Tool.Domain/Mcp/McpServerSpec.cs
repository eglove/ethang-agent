namespace eThangAgent.ToolDomain.Mcp;

/// <summary>Where a configured server lives: the global scope every workspace sees, or
///     the workspace that configured it.</summary>
public enum McpServerScope
{
  /// <summary>Visible to every workspace.</summary>
  Global,

  /// <summary>Visible only to the configuring workspace.</summary>
  Workspace,
}

/// <summary>The validated launch shape of one MCP server, as a caller supplies it: name,
///     transport, command or URL, args, env, headers, scope, pinned version, and gate.
///     Parsed strictly from tool input - every field typed, nothing coerced - and rendered
///     back to the stored row's JSON column shapes (args a JSON array, env and headers JSON
///     objects), so what a caller supplies and what the store reads back are one shape.</summary>
// CA1054/CA1056: deliberately raw text - for stdio transports this is a command
// (not a URI at all), and validation is the parser's job, not the record's.
#pragma warning disable CA1054, CA1056
public sealed record McpServerSpec(
    string Name,
    McpTransport Transport,
    string CommandOrUrl,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env,
    IReadOnlyDictionary<string, string> Headers,
    McpServerScope Scope,
    string? PinnedVersion,
    McpGateMode GateMode)
#pragma warning restore CA1054, CA1056
{
  /// <summary>The launch arguments as the stored JSON array text.</summary>
  public string ArgsJson => McpServerSpecJson.Array(Args);

  /// <summary>The environment entries as the stored JSON object text.</summary>
  public string EnvJson => McpServerSpecJson.Object(Env);

  /// <summary>The static HTTP headers as the stored JSON object text.</summary>
  public string HeadersJson => McpServerSpecJson.Object(Headers);

  /// <summary>The scope's stored column value: null for global, the workspace id for a
  ///     workspace-scoped row.</summary>
  public string? WorkspaceId(string workspaceId) =>
      Scope == McpServerScope.Global ? null : workspaceId;

  /// <summary>The config row this spec describes, for a store that has not assigned an
  ///     id yet.</summary>
  public McpServerConfig ToConfig(string workspaceId, McpApprovalState approval)
  {
    ArgumentNullException.ThrowIfNull(workspaceId);
    return new McpServerConfig(
        0,
        Name,
        Transport,
        CommandOrUrl,
        ArgsJson,
        EnvJson,
        HeadersJson,
        WorkspaceId(workspaceId),
        approval,
        PinnedVersion,
        DateTimeOffset.UtcNow,
        GateMode);
  }

  /// <summary>The stored row's launch fields replaced by this spec's, keeping the row's
  ///     id, scope, and creation time.</summary>
  public McpServerConfig AppliedTo(McpServerConfig existing, McpApprovalState approval)
  {
    ArgumentNullException.ThrowIfNull(existing);
    return existing with
    {
      Name = Name,
      Transport = Transport,
      CommandOrUrl = CommandOrUrl,
      ArgsJson = ArgsJson,
      EnvJson = EnvJson,
      HeadersJson = HeadersJson,
      ApprovalState = approval,
      PinnedVersion = PinnedVersion,
      GateMode = GateMode,
    };
  }

  /// <summary>Whether this launch differs from the stored row's in any trust-relevant
  ///     field - name, transport, command or URL, args, env, headers, pinned version.
  ///     Scope and gate are not launch identity: changing either keeps the approval the
  ///     user already gave.</summary>
  public bool ChangesLaunch(McpServerConfig existing)
  {
    ArgumentNullException.ThrowIfNull(existing);
    return !string.Equals(existing.Name, Name, StringComparison.Ordinal)
        || existing.Transport != Transport
        || !string.Equals(existing.CommandOrUrl, CommandOrUrl, StringComparison.Ordinal)
        || !string.Equals(existing.ArgsJson, ArgsJson, StringComparison.Ordinal)
        || !string.Equals(existing.EnvJson, EnvJson, StringComparison.Ordinal)
        || !string.Equals(existing.HeadersJson, HeadersJson, StringComparison.Ordinal)
        || !string.Equals(existing.PinnedVersion ?? "", PinnedVersion ?? "", StringComparison.Ordinal);
  }

  /// <summary>The first problem with this spec, or null when it is usable: a name, a
  ///     command or URL, and an absolute URL for the http transport.</summary>
  public string? Problem() =>
      string.IsNullOrWhiteSpace(Name) ? "A server name is required." : LaunchProblem();

  /// <summary>The problem with the command or URL, or null when it is usable.</summary>
  private string? LaunchProblem() =>
      string.IsNullOrWhiteSpace(CommandOrUrl) ? MissingCommand() : EndpointProblem();

  /// <summary>The message for a spec with no command or URL, per transport.</summary>
  private string MissingCommand() => Transport switch
  {
    McpTransport.Stdio => "A stdio server needs its launch command.",
    McpTransport.Http => "An HTTP server needs its endpoint URL.",
    _ => "A server needs a command or URL.",
  };

  /// <summary>The message for an http endpoint that is not an absolute URL, or null.</summary>
  private string? EndpointProblem()
  {
    return Transport == McpTransport.Http && !Uri.IsWellFormedUriString(CommandOrUrl, UriKind.Absolute)
        ? "The HTTP endpoint must be an absolute URL."
        : null;
  }
}
