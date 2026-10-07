namespace eThangAgent.ToolDomain.Mcp;

/// <summary>The ambient MCP grant scope's storage (issue #108): AsyncLocal, so the
///     value flows down a child run's async flow (the loop, its tool executions) but
///     never sideways — concurrent children in one process can never observe each
///     other's grants. The composition's scope implementation serves the entries;
///     the spawner sets it around a child run and restores the previous value
///     afterwards (the workspace anchor scope's save/restore pattern).</summary>
public static class AmbientMcpGrantScope
{
  private static readonly AsyncLocal<IMcpGrantScope?> CurrentValue = new();

  /// <summary>The ambient scope for the current async flow, or null when unset
  ///     (root agents, ungranted children, legacy wiring — full-reach default).</summary>
  public static IMcpGrantScope? Current
  {
    get => CurrentValue.Value;
    set => CurrentValue.Value = value;
  }
}
