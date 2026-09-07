using eThangAgent.AgentDomain;

namespace eThangAgent.Composition;

/// <summary>Per-container workspace anchor scope backed by <see cref="AsyncLocal{T}"/>:
///     the ambient value flows down a child run's async flow (the loop, its tool
///     executions, and any nested exec) but never sideways, so concurrent children in
///     one process can never observe each other's anchors. One instance per session
///     container; the spawner writes it around an anchored child's run and restores the
///     previous value afterwards, and the exec engine's workspace resolver reads it
///     ahead of the session workspace.</summary>
public sealed class SessionWorkspaceAnchorScope : IWorkspaceAnchorScope
{
  private static readonly AsyncLocal<string?> CurrentValue = new();

  /// <inheritdoc />
  public string? Current
  {
    get => CurrentValue.Value;
    set => CurrentValue.Value = value;
  }
}
