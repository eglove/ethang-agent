using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed record ExecProgram(string Text)
{
  public static Result<ExecProgram> Create(string? program, ExecOptions options)
  {
    ArgumentNullException.ThrowIfNull(options);
    if (program is null || program.Length == 0)
    {
      return Result.Failure<ExecProgram>(new DomainError("ExecProgramRequired",
          "'program' must be a non-empty string."));
    }

    bool withinBudget = program.Length <= options.MaxProgramChars;
    return withinBudget
        ? Result.Success(new ExecProgram(program))
        : Result.Failure<ExecProgram>(new DomainError("ExecProgramTooLarge",
            $"'program' is {program.Length} characters; maximum is {options.MaxProgramChars}."));
  }
}
