namespace eThangAgent.ToolDomain.Mcp;

/// <summary>The MCP grant scope over one resolved effective entry set (issue #108):
///     the entries the spawn command resolved from the parent's effective set and the
///     validated grants (exact ids and namespace patterns, the ToolGrantPolicy's
///     output). Matching is the policy's reach semantics: a pattern entry covers every
///     id under its namespace (mcp.github.* covers mcp.github.create_issue); a bare
///     dispatch root covers the whole namespace (full reach — the default surface's
///     shape); a root WITH patterns reaches the patterns' union only.</summary>
public sealed class McpGrantScope(IReadOnlySet<string> entries) : IMcpGrantScope
{
  private readonly IReadOnlySet<string> _entries = entries ?? throw new ArgumentNullException(nameof(entries));

  /// <inheritdoc />
  public string? RefusalFor(string resolvedId)
  {
    ArgumentNullException.ThrowIfNull(resolvedId);
    return Covers(resolvedId) ? null : GrantViolation.For(resolvedId);
  }

  /// <inheritdoc />
  public bool Reachable(string serverName)
  {
    ArgumentNullException.ThrowIfNull(serverName);
    return Covers("mcp." + serverName);
  }

  private bool Covers(string resolvedId)
  {
    bool hasRoot = false;
    bool hasPatterns = false;
    foreach (string entry in _entries)
    {
      if (IsPattern(entry))
      {
        hasPatterns = true;
        string prefix = entry[..^2];
        if (resolvedId == prefix || resolvedId.StartsWith(prefix + ".", StringComparison.Ordinal))
        {
          return true;
        }
      }
      else if (entry == resolvedId)
      {
        return true; // an exact entry covering the id itself
      }
      else if (resolvedId.StartsWith(entry + ".", StringComparison.Ordinal))
      {
        hasRoot = true; // a bare dispatch root over this id's namespace
      }
    }

    // A bare root with no patterns = full reach over its namespace (the default
    // surface's shape); with patterns, the patterns govern exclusively.
    return hasRoot && !hasPatterns;
  }

  private static bool IsPattern(string entry)
      => entry.EndsWith(".*", StringComparison.Ordinal) && entry.Length > 2;
}
