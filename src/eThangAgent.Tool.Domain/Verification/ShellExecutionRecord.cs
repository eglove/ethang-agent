namespace eThangAgent.ToolDomain.Verification;

/// <summary>One completed external command run reported by the exec engine's
///     Shell surface. Tokens are the post-argv-split command line; exit code is
///     the native process exit code; the timestamps bound the run.</summary>
public sealed record ShellExecutionRecord(
    IReadOnlyList<string> Tokens,
    int ExitCode,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc);
