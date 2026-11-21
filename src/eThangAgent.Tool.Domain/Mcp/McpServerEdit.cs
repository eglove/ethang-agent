namespace eThangAgent.ToolDomain.Mcp;

/// <summary>A partial change to one configured server: the fields the caller supplied,
///     each null when it was omitted. Absent means keep the stored value - an edit never
///     blanks a field it did not mention. The access composes the result over the stored
///     row, so a caller can rename a server without restating its launch.</summary>
/// <param name="Name">The new name, or null to keep it.</param>
/// <param name="Transport">The new transport, or null to keep it.</param>
/// <param name="CommandOrUrl">The new command or URL, or null to keep it.</param>
/// <param name="Args">The new launch arguments, or null to keep them.</param>
/// <param name="Env">The new environment entries, or null to keep them.</param>
/// <param name="Headers">The new static headers, or null to keep them.</param>
/// <param name="Scope">The new scope, or null to keep it.</param>
/// <param name="PinnedVersion">The new pinned version; null keeps it, an empty string clears it.</param>
/// <param name="GateMode">The new per-call gate, or null to keep it.</param>
// CA1054/CA1056: deliberately raw text - for stdio transports this is a command
// (not a URI at all); the same named deviation as McpServerConfig.
#pragma warning disable CA1054, CA1056
public sealed record McpServerEdit(
    string? Name,
    McpTransport? Transport,
    string? CommandOrUrl,
    IReadOnlyList<string>? Args,
    IReadOnlyDictionary<string, string>? Env,
    IReadOnlyDictionary<string, string>? Headers,
    McpServerScope? Scope,
    string? PinnedVersion,
    McpGateMode? GateMode)
#pragma warning restore CA1054, CA1056
{
  /// <summary>Whether the caller supplied nothing at all - an edit that would change
  ///     no field is refused rather than written as a no-op.</summary>
  public bool IsEmpty => Name is null && Transport is null && CommandOrUrl is null
      && Args is null && Env is null && Headers is null && Scope is null
      && PinnedVersion is null && GateMode is null;

  /// <summary>The stored row with every supplied field replaced and every omitted field
  ///     kept. The approval state is the caller's decision: it is not part of an edit.
  /// </summary>
  public McpServerConfig AppliedTo(McpServerConfig existing, string workspaceId, McpApprovalState approval)
  {
    ArgumentNullException.ThrowIfNull(existing);
    return existing with
    {
      Name = Name ?? existing.Name,
      Transport = Transport ?? existing.Transport,
      CommandOrUrl = CommandOrUrl ?? existing.CommandOrUrl,
      ArgsJson = Args is null ? existing.ArgsJson : McpServerSpecJson.Array(Args),
      EnvJson = Env is null ? existing.EnvJson : McpServerSpecJson.Object(Env),
      HeadersJson = Headers is null ? existing.HeadersJson : McpServerSpecJson.Object(Headers),
      WorkspaceId = ScopeId(existing, workspaceId),
      ApprovalState = approval,
      PinnedVersion = Pinned(existing),
      GateMode = GateMode ?? existing.GateMode,
    };
  }

  /// <summary>The row's scope column after the edit: an omitted scope keeps the stored
  ///     one, and an explicit scope is the workspace id or null for global.</summary>
  private string? ScopeId(McpServerConfig existing, string workspaceId)
  {
    return Scope switch
    {
      null => existing.WorkspaceId,
      McpServerScope.Workspace => workspaceId,
      McpServerScope.Global => null,
      _ => null,
    };
  }

  /// <summary>The pinned version after the edit: omitted keeps the stored pin, and a
  ///     blank value clears it.</summary>
  private string? Pinned(McpServerConfig existing) =>
      PinnedVersion is null ? existing.PinnedVersion : NullIfBlank(PinnedVersion);

  private static string? NullIfBlank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
