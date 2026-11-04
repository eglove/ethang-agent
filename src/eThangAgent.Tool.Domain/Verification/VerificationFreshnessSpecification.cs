namespace eThangAgent.ToolDomain.Verification;

/// <summary>The Iron Law as a predicate: the changed files are covered only when a command that
///     exited 0 STARTED after the newest change. Start comparison is deliberate - an edit landing
///     mid-run carries an mtime later than the run start, so the gate fires.
///     <para>ANY COMMAND COUNTS. The harness ships no command set and never learns a project's
///     tooling: whatever the session ran and watched exit 0 is its verification, whether that is
///     a build, a test runner, a linter, or a script nobody has heard of.</para>
///     Nothing changed means nothing to verify: empty changedFiles passes.</summary>
public static class VerificationFreshnessSpecification
{
  /// <summary>True when a successful run started after every changed-file modification, or when
  ///     nothing changed.</summary>
  public static bool IsSatisfiedBy(
      IReadOnlyList<ShellExecutionRecord> ledger,
      IReadOnlyList<(string Path, DateTimeOffset ModifiedUtc)> changedFiles)
  {
    ArgumentNullException.ThrowIfNull(ledger);
    ArgumentNullException.ThrowIfNull(changedFiles);
    if (changedFiles.Count == 0)
    {
      return true;
    }

    DateTimeOffset newest = changedFiles.Max(c => c.ModifiedUtc);
    return ledger.Any(r => r.ExitCode == 0 && r.StartedUtc > newest);
  }

  /// <summary>The most recently STARTED run, whatever its exit code, or null when the ledger
  ///     holds none. This is the run a completion claim is judged against when nothing has
  ///     succeeded: its exit code is the failure the refusal names.</summary>
  public static ShellExecutionRecord? NewestRun(IReadOnlyList<ShellExecutionRecord> ledger)
  {
    ArgumentNullException.ThrowIfNull(ledger);
    return ledger
        .OrderByDescending(r => r.StartedUtc)
        .FirstOrDefault();
  }

  /// <summary>The most recently STARTED successful run, or null when nothing has exited 0 in
  ///     this session. This is the evidence a completion claim rests on, and the line the commit
  ///     gate's warning names.</summary>
  public static ShellExecutionRecord? NewestSuccessfulRun(IReadOnlyList<ShellExecutionRecord> ledger)
  {
    ArgumentNullException.ThrowIfNull(ledger);
    return ledger
        .Where(r => r.ExitCode == 0)
        .OrderByDescending(r => r.StartedUtc)
        .FirstOrDefault();
  }
}
