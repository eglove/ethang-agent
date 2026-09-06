namespace eThangAgent.ToolDomain;

/// <summary>One completed user command run (the ! chat command): the assigned
///     workspace-scoped id, the command text, its exit code, whether the budget
///     expired (partial output), the captured output, and when it ran.</summary>
public sealed record CommandRun(
    int Id,
    string Command,
    int ExitCode,
    bool TimedOut,
    string Output,
    DateTimeOffset RanAt);
