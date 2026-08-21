using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed record ExecProgram(string Text)
{
    public static Result<ExecProgram> Create(string? program, ExecOptions options)
    {
        if (program is null || program.Length == 0)
            return Result<ExecProgram>.Failure(new Error("ExecProgramRequired",
                "'program' must be a non-empty string."));
        if (program.Length > options.MaxProgramChars)
            return Result<ExecProgram>.Failure(new Error("ExecProgramTooLarge",
                $"'program' is {program.Length} characters; maximum is {options.MaxProgramChars}."));
        return Result<ExecProgram>.Success(new ExecProgram(program));
    }
}
