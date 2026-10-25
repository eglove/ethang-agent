namespace eThangAgent.StateDomain;

/// <summary>Runs ONE goal/check command as a SHELL command line.
///     <para>WHY A SEPARATE SEAM: <see cref="IEvidenceRunner"/> evaluates C# boolean
///     EXPRESSIONS - that is what <c>state.transition</c>'s evidence strings are, and
///     <c>state.verify</c> compiles them. A <c>goal/check</c> entry is a COMMAND LINE
///     (<c>dotnet build</c>, <c>go test ./...</c>), so running it through the evidence
///     runner compiled it as C# and failed every time: measured 2026-10-25, storing
///     <c>["dotnet --version"]</c> at goal/check made <c>checkgoal</c> report
///     "The name 'dotnet' does not exist in the current context". The two kinds of
///     string look alike and mean nothing alike, so they get different seams.</para></summary>
public interface IGoalCommandRunner
{
  /// <summary>Runs <paramref name="command"/> at <paramref name="workingDirectory"/>.
  ///     A command that exits nonzero is a completed run with a failing exit code, not
  ///     an error; only a failure to START surfaces as an error.</summary>
  Task<EvidenceResult> RunAsync(string workingDirectory, string command,
      CancellationToken ct = default);
}
