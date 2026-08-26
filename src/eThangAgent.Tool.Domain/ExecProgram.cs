using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed record ExecProgram(string Text)
{
  public static Result<ExecProgram> Create(string? program, ExecOptions options)
  {
    ArgumentNullException.ThrowIfNull(options);
    return program is null || program.Length == 0
      ? Result.Failure<ExecProgram>(new DomainError("ExecProgramRequired",
          "'program' must be a non-empty string."))
      : program.Length > options.MaxProgramChars
      ? Result.Failure<ExecProgram>(new DomainError("ExecProgramTooLarge",
          $"'program' is {program.Length} characters; maximum is {options.MaxProgramChars}."))
      : Result.Success<ExecProgram>(new ExecProgram(program));
  }
}
