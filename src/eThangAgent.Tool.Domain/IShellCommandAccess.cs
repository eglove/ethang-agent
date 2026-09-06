using eThangAgent.SharedKernel;
namespace eThangAgent.ToolDomain;

/// <summary>Result of one shell command run: the process ran (it may itself have
///     failed — the exit code carries that); <see cref="TimedOut"/> marks a budget
///     expiry whose partial output was still captured.</summary>
public sealed record ShellRun(int ExitCode, string Output, bool TimedOut);

/// <summary>Runs a user command through the machine's shell at an anchor directory.
///     The implementation owns shell discovery — callers never name a shell.
///     The output merges stdout and stderr in arrival order (a terminal-like view);
///     non-terminating errors (stderr) and the exit code stay distinguishable in
///     the run record's exit code, not in the seam's result: a command that exits
///     nonzero is a successful RUN whose command failed.</summary>
public interface IShellCommandAccess
{
  /// <summary>Runs <paramref name="command"/> with <paramref name="workingDirectory"/>
  ///     as the process working directory. Expected failures (no shell on the machine,
  ///     transport failures) flow as Result errors — never exceptions.</summary>
  Task<Result<ShellRun>> RunAsync(string workingDirectory, string command,
      TimeSpan timeout, CancellationToken ct = default);
}
