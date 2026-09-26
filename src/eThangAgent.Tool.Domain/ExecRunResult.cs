namespace eThangAgent.ToolDomain;

public sealed record ExecRunResult(
    ExecRunStatus Status,
    string Output,
    IReadOnlyList<string> ErrorLines,
    string? ErrorMessage = null,
    int NestedDispatchCount = 0)
{
  public static ExecRunResult Completed(string output, int nestedDispatchCount = 0)
      => new(ExecRunStatus.Completed, output, [], NestedDispatchCount: nestedDispatchCount);
}
