namespace eThangAgent.ToolDomain;

public sealed record ExecActivity(
    string ProgramPreview,
    ExecRunStatus Status,
    int OutputChars,
    TimeSpan Duration,
    bool IsError);
