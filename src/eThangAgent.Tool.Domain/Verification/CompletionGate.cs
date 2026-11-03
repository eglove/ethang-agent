using System.Globalization;

namespace eThangAgent.ToolDomain.Verification;

/// <summary>Decides whether a completion claim may proceed, from the session's own shell
///     ledger: a claim is backed by a verification-class command that exited 0 and started
///     after the newest workspace change.
///     <para>THE HARNESS KNOWS NOTHING ABOUT LINTERS. It ships no command set of its own and
///     never learns a project's tooling; the model runs whatever verifies the work, and this
///     gate only asks whether the answer is still current. A session whose verification
///     command nobody has run has verified nothing, and the gate says so.</para>
///     <para>EVERY REFUSAL NAMES ITS EXIT: run verification, or abandon the work. A claim
///     that cannot be backed is not a claim.</para></summary>
public sealed class CompletionGate(
    IVerificationLedger ledger,
    VerificationFreshnessSpecification freshness,
    IWorkspaceSnapshotAccess snapshots,
    Func<string> workspaceRoot,
    bool enabled = true)
{
  private readonly IVerificationLedger _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
  private readonly VerificationFreshnessSpecification _freshness =
      freshness ?? throw new ArgumentNullException(nameof(freshness));
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
  ///     nearest remedy: nothing ran, the newest run failed, or it passed before a later change.
  ///     </summary>
  private CompletionVerdict Classify(IReadOnlyList<ShellExecutionRecord> runs,
      WorkspaceSnapshot workspace) =>
      _freshness.NewestRun(runs) is { } newest
          ? Classify(newest, workspace)
          : new CompletionVerdict(VerificationGateStatus.NeverRan, Refusal(
              "no verification command has run in this session",
              "run whatever verifies this work and confirm exit code 0"));

  /// <summary>One run's own verdict: a nonzero exit is a failure whatever the tree looks like,
  ///     and a passing run is only good while the tree still matches it.</summary>
  private static CompletionVerdict Classify(ShellExecutionRecord newest,
      WorkspaceSnapshot workspace) =>
      newest.ExitCode == 0
          ? Freshness(newest, workspace)
          : new CompletionVerdict(VerificationGateStatus.Failed, Refusal(
              string.Create(CultureInfo.InvariantCulture,
                  $"the last verification command ({Describe(newest)}) exited {newest.ExitCode} at {newest.FinishedUtc:u}"),
              "fix the failure, then run it again until it exits 0"));

  /// <summary>A passing run covers the claim only while no workspace file changed after it
  ///     started: an edit landing mid-run carries a later write time, so it invalidates the run.
  ///     </summary>
  private static CompletionVerdict Freshness(ShellExecutionRecord newest,
      WorkspaceSnapshot workspace) =>
      ChangedSince(workspace, newest.StartedUtc).Any()
          ? new CompletionVerdict(VerificationGateStatus.Stale, Refusal(
              string.Create(CultureInfo.InvariantCulture,
                  $"the last verification command ({Describe(newest)}) exited 0 at {newest.FinishedUtc:u}, but the workspace has CHANGED since that run"),
              "run it again so the passing result covers the current code"))
          : new CompletionVerdict(VerificationGateStatus.Fresh,
              string.Create(CultureInfo.InvariantCulture,
                  $"[completion gate] {Describe(newest)} exited 0 at {newest.FinishedUtc:u} and nothing has changed since."));

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
