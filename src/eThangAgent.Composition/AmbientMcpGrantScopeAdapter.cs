namespace eThangAgent.Composition;

/// <summary>The composition's MCP grant scope (issue #108): reads the ambient scope
///     storage — the spawner writes the child's resolved entries around its run, and
///     this adapter is what the session's mcp tool consults at dispatch time. The
///     indirection exists because the tool is a session singleton shared by root and
///     children alike: the ambient value, not the tool instance, carries the per-run
///     grants.</summary>
public sealed class AmbientMcpGrantScopeAdapter : ToolDomain.Mcp.IMcpGrantScope
{
  /// <inheritdoc />
  public string? RefusalFor(string resolvedId)
      => ToolDomain.Mcp.AmbientMcpGrantScope.Current?.RefusalFor(resolvedId);

  /// <inheritdoc />
  public bool Reachable(string serverName)
      => ToolDomain.Mcp.AmbientMcpGrantScope.Current?.Reachable(serverName) ?? true;
}
