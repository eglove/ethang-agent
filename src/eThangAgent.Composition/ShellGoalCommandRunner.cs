using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition;

/// <summary>Composition adapter: runs a <c>goal/check</c> entry as a SHELL command line
///     through the same <see cref="IShellCommandAccess"/> the user's <c>!</c> commands use
///     (issue #283).
///     <para>This is the defect the issue's premise missed: <c>checkgoal</c> used to run
///     these strings through the EVIDENCE runner, which compiles C# boolean expressions.
///     A command line like <c>dotnet build</c> compiled as C# and failed every time, so
///     the mechanism the issue proposed to reuse could never have gated anything. The
///     strings are commands, so they get command semantics.</para></summary>
internal sealed class ShellGoalCommandRunner(IShellCommandAccess shell, GoalRunnerOptions options)
    : IGoalCommandRunner
{
  public async Task<EvidenceResult> RunAsync(string workingDirectory, string command,
      CancellationToken ct = default)
  {
    Result<ShellRun> run = await shell
        .RunAsync(workingDirectory, command, options.Timeout, ct)
        .ConfigureAwait(false);

    if (!run.IsSuccess)
    {
      return new EvidenceResult(command, false, run.Error.Message);
    }

    // A nonzero exit is a completed run whose command failed - that is a gate FAILURE,
    // not an infrastructure error, and the exit code is what the project's own tooling
    // decided. A timeout is a failure too: a gate that did not finish has not passed.
    return run.Value.TimedOut
        ? new EvidenceResult(command, false,
            $"timed out after {options.Timeout.TotalMinutes:0} minute(s) (partial output captured)")
        : Classify(command, run.Value.ExitCode);
  }

  /// <summary>Exit 0 is a pass; anything else is the project's own tooling saying no.</summary>
  private static EvidenceResult Classify(string command, int exitCode) =>
      exitCode == 0
          ? new EvidenceResult(command, true, string.Empty)
          : new EvidenceResult(command, false, $"exit code {exitCode}");
}
