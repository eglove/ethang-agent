namespace eThangAgent.ToolDomain;

/// <summary>Carries NO execution budget: the required per-call timeoutSeconds argument
/// is the sole authority (enforced by ToolExecution; surfaced as Error [ToolTimeout]).</summary>
public sealed record ExecOptions
{
  public int MaxProgramChars { get; init; } = 64 * 1024;

  public static ExecOptions Default { get; } = new();
}
