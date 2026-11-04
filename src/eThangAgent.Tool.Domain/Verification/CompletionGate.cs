using System.Globalization;

namespace eThangAgent.ToolDomain.Verification;

/// <summary>Decides whether a completion claim may proceed, from the session's own shell ledger:
///     a claim is backed by a command that exited 0 and started after the newest workspace change.
///     <para>THE HARNESS KNOWS NOTHING ABOUT LINTERS. It ships no command set of its own and never
///     learns a project's tooling; ANY command that exits 0 counts, so a workspace of any shape is
///     verifiable by whatever its session chose to run. The gate only asks whether that answer is
///     still current.</para>
///     <para>A SUCCESSFUL RUN OUTRANKS A FAILED ONE. The newest successful run is the evidence a
///     claim rests on; a later failure is named only when nothing has succeeded at all, so a
///     session that fixed a failure and re-ran it is not judged by the run it already repaired.
///     </para>
///     <para>EVERY REFUSAL NAMES ITS EXIT: run verification, or abandon the work. A claim that
///     cannot be backed is not a claim.</para></summary>
public sealed class CompletionGate(
    IVerificationLedger ledger,
    IWorkspaceSnapshotAccess snapshots,
    Func<string> workspaceRoot,
    bool enabled = true)
{
  private readonly IVerificationLedger _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
  private readonly IWorkspaceSnapshotAccess _snapshots =
      snapshots ?? throw new ArgumentNullException(nameof(snapshots));
  private readonly Func<string> _workspaceRoot =
      workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot));

  /// <summary>Configuration switch: false stands the gate down entirely.</summary>
  public bool Enabled { get; } = enabled;

  /// <summary>Evaluates the claim against the ledger and the workspace as it stands now.</summary>
  /// <param name="ct">Cancellation.</param>
  public async Task<CompletionVerdict> EvaluateAsync(CancellationToken ct = default)
  {
    if (!Enabled)
    {
      return new CompletionVerdict(VerificationGateStatus.Fresh, string.Empty);
    }

    IReadOnlyList<ShellExecutionRecord> runs = _ledger.Snapshot();
    WorkspaceSnapshot workspace;
    try
    {
      workspace = await _snapshots.CaptureAsync(_workspaceRoot(), ct).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
      throw;
    }
    // Named decision (CA1031): an unreadable workspace must not become a refusal the
    // claimant cannot escape - it becomes the honest 'cannot tell' state instead.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception)
    {
      return Unverifiable("the workspace could not be read");
    }
#pragma warning restore CA1031 // Do not catch general exception types

    return workspace.IsComplete
        ? Classify(runs, workspace)
        : Unverifiable("the workspace walk was truncated");
  }

  /// <summary>The three facts the ledger and the workspace settle, in the order that names the
  ///     nearest remedy: nothing ran, nothing succeeded, or the newest success predates a later
  ///     change.</summary>
  private static CompletionVerdict Classify(IReadOnlyList<ShellExecutionRecord> runs,
      WorkspaceSnapshot workspace) =>
      VerificationFreshnessSpecification.NewestSuccessfulRun(runs) is { } success
          ? Freshness(success, workspace)
          : NoSuccess(runs);

  /// <summary>Nothing has exited 0: either the newest run failed, or nothing ran at all. The two
  ///     are different remedies, so they are different verdicts.</summary>
  private static CompletionVerdict NoSuccess(IReadOnlyList<ShellExecutionRecord> runs) =>
      VerificationFreshnessSpecification.NewestRun(runs) is { } failed
          ? new CompletionVerdict(VerificationGateStatus.Failed, Refusal(
              string.Create(CultureInfo.InvariantCulture,
                  $"the last command ({Describe(failed)}) exited {failed.ExitCode} at {failed.FinishedUtc:u} and nothing has exited 0 since"),
              "fix the failure, then run it again until it exits 0"))
          : new CompletionVerdict(VerificationGateStatus.NeverRan, Refusal(
              "no command has exited 0 in this session",
              "run whatever verifies this work and confirm exit code 0"));

  /// <summary>A successful run covers the claim only while no workspace file changed after it
  ///     started: an edit landing mid-run carries a later write time, so it invalidates the run.
  ///     </summary>
  private static CompletionVerdict Freshness(ShellExecutionRecord success,
      WorkspaceSnapshot workspace) =>
      ChangedSince(workspace, success.StartedUtc).Any()
          ? new CompletionVerdict(VerificationGateStatus.Stale, Refusal(
              string.Create(CultureInfo.InvariantCulture,
                  $"the last successful command ({Describe(success)}) exited 0 at {success.FinishedUtc:u}, but the workspace has CHANGED since that run"),
              "run it again so the passing result covers the current code"))
          : new CompletionVerdict(VerificationGateStatus.Fresh,
              string.Create(CultureInfo.InvariantCulture,
                  $"[completion gate] {Describe(success)} exited 0 at {success.FinishedUtc:u} and nothing has changed since."));

  /// <summary>Every workspace file written after <paramref name="sinceUtc"/>, by last-write
  ///     time. The write time is what a run can be compared against; content equality would
  ///     need the tree as it stood at the run, which no snapshot here holds.</summary>
  private static IEnumerable<(string Path, DateTimeOffset ModifiedUtc)> ChangedSince(
      WorkspaceSnapshot workspace, DateTimeOffset sinceUtc) =>
      workspace.Files
          .Select(file => (file.Key, Modified: new DateTimeOffset(
              DateTime.SpecifyKind(file.Value.LastWriteUtc, DateTimeKind.Utc))))
          .Where(file => file.Modified > sinceUtc)
          .Select(file => (file.Key, file.Modified));

  /// <summary>The command line as the target process received it, so the refusal names what
  ///     actually ran rather than what was intended.</summary>
  private static string Describe(ShellExecutionRecord record) => string.Join(' ', record.Tokens);

  private static CompletionVerdict Unverifiable(string why) =>
      new(VerificationGateStatus.Unverifiable,
          $"[completion gate] Freshness cannot be verified ({why}). This does not block the "
          + "claim.");

  /// <summary>The refusal sentence, naming what happened and the exact remedy. Both attach
  ///     points render this verbatim, so the model meets the same words wherever it hits the
  ///     gate.</summary>
  private static string Refusal(string what, string remedy) =>
      $"[completion gate] This claim is unbacked: {what}. "
      + $"To claim completion, {remedy}. To drop the work instead, set the plan Abandoned - "
      + "that is always allowed and is the honest exit when the work is not finished.";
}
