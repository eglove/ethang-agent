namespace eThangAgent.ToolDomain;

/// <summary>Carries NO execution budget: the required per-call timeoutSeconds argument
/// is the sole authority (enforced by ToolExecution; surfaced as Error [ToolTimeout]).</summary>
public sealed record ExecOptions
{
  public int MaxProgramChars { get; init; } = 64 * 1024;
  public int MaxOutputChars { get; init; } = 50 * 1024;
  public int MaxErrorChars { get; init; } = 20 * 1024;
  public int MaxParseErrors { get; init; } = 10;
  public string ArtifactDirectory { get; init; } = Path.Combine(
      Environment.GetEnvironmentVariable("TEMP") ?? ".", "eThangAgent", "exec-artifacts");

  public static ExecOptions Default { get; } = new();
}
