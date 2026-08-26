using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public interface IExecEngine
{
  Task<Result<IReadOnlyList<ExecParseError>>> ValidateAsync(
      ExecProgram program, CancellationToken ct = default);

  Task<ExecRunResult> ExecuteAsync(ExecProgram program, CancellationToken ct = default);
}
