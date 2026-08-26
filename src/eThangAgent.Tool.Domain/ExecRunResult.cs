namespace eThangAgent.ToolDomain;

public sealed record ExecRunResult(
    ExecRunStatus Status,
    string Output,
    IReadOnlyList<string> ErrorLines,
    string? ErrorMessage = null)
{
  public static ExecRunResult Completed(string output)
      => new(ExecRunStatus.Completed, output, []);
}
