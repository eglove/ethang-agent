namespace eThangAgent.ToolDomain.Verification;

/// <summary>The Iron Law as a predicate: the changed files are covered only
///     when a successful verification-class run STARTED after the newest
///     change. Start comparison is deliberate - an edit landing mid-run
///     carries an mtime later than the run start, so the gate fires.
///     Nothing changed means nothing to verify: empty changedFiles passes.</summary>
public sealed class VerificationFreshnessSpecification(VerificationCommandSpecification commands)
{
  private readonly VerificationCommandSpecification _commands =
      commands ?? throw new ArgumentNullException(nameof(commands));

  /// <summary>True when a verification-class successful run started after
  ///     every changed-file modification, or when nothing changed.</summary>
  public bool IsSatisfiedBy(
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
    return ledger.Any(r => r.ExitCode == 0
        && r.StartedUtc > newest
        && _commands.IsSatisfiedBy(r));
  }
  /// <summary>True when the ledger holds ANY verification-class attempt,
  ///     success or failure: the turn gate's escape hatch asks whether a
  ///     verification command is KNOWN here, and a watched failure proves it is
  ///     (issue #256). Delegates to the command specification's exit-agnostic
  ///     class query.</summary>
  public bool HasKnownCommand(IReadOnlyList<ShellExecutionRecord> ledger)
  {
    ArgumentNullException.ThrowIfNull(ledger);
    return _commands.HasKnownCommand(ledger);
  }
}
