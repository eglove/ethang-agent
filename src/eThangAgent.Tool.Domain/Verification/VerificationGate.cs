namespace eThangAgent.ToolDomain.Verification;

/// <summary>The verdict of one gate evaluation: whether the changed files are covered by a fresh
///     successful run, with the counts and the last-run summary the warning text carries.</summary>
public readonly record struct VerificationVerdict(
    bool Verified,
    int ChangedCount,
    DateTimeOffset? NewestChangeUtc,
    string? LastSuccessfulRunSummary);

/// <summary>Shared front for both gates (commit and turn): evaluates the changed-file set against
///     the session ledger. A disabled gate verifies everything.
///     <para>ANY EXIT-0 COMMAND IS VERIFICATION. There is no configured command set to match
///     against, so a workspace of any shape - a .NET solution, a Node package, a Makefile, a
///     folder of scripts - is covered by whatever its session ran and watched succeed.</para>
///     </summary>
public sealed class VerificationGate(IVerificationLedger ledger, bool enabled)
{
  private readonly IVerificationLedger _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));

  /// <summary>Configuration switch: false means byte-identical legacy behavior.</summary>
  public bool Enabled { get; } = enabled;

  /// <summary>Evaluates the changed files (already resolved by the caller) against the ledger.
  ///     Nothing changed always verifies.</summary>
  public VerificationVerdict Evaluate(IReadOnlyList<(string Path, DateTimeOffset ModifiedUtc)> changedFiles)
  {
    ArgumentNullException.ThrowIfNull(changedFiles);
    if (!Enabled)
    {
      // A disabled gate renders no text and does no work: the ledger is not even read.
      return new VerificationVerdict(true, changedFiles.Count, null, null);
    }

    DateTimeOffset? newest = 0 < changedFiles.Count ? changedFiles.Max(c => c.ModifiedUtc) : null;
    IReadOnlyList<ShellExecutionRecord> snapshot = _ledger.Snapshot();
    bool fresh = VerificationFreshnessSpecification.IsSatisfiedBy(snapshot, changedFiles);
    string? summary = VerificationFreshnessSpecification.NewestSuccessfulRun(snapshot) is { } newestSuccess
        ? string.Join(' ', newestSuccess.Tokens) + " at "
            + newestSuccess.StartedUtc.ToString("u", System.Globalization.CultureInfo.InvariantCulture)
        : null;

    return new VerificationVerdict(fresh, changedFiles.Count, newest, summary);
  }

  /// <summary>The commit gate's warning contract: the commit proceeds; this line appends to its
  ///     output when changed files lack a fresh successful run. Callers invoke it only on an
  ///     unverified verdict.</summary>
  public static string WarningLine(VerificationVerdict v)
  {
    string lastSuccess = v.LastSuccessfulRunSummary ?? "none";
    return "[warning] verification gate: " + v.ChangedCount + " file(s) changed; newest change "
        + v.NewestChangeUtc?.ToString("u", System.Globalization.CultureInfo.InvariantCulture)
        + "; last successful command: " + lastSuccess
        + ". No command has exited 0 since the newest change; run whatever verifies this work"
        + " and confirm exit code 0 before claiming completion.";
  }

  /// <summary>The turn gate's nudge contract, verbatim: null when verified or nothing changed;
  ///     otherwise an ADVISORY note the handler appends as a System message (one per turn, never
  ///     a loop). The note is weighed when the model claims completion, never obeyed as a turn
  ///     precondition.</summary>
  public string? NudgeLine(IReadOnlyList<(string Path, DateTimeOffset ModifiedUtc)> changedFiles)
  {
    if (!Enabled)
    {
      return null;
    }

    VerificationVerdict v = Evaluate(changedFiles);
    if (v.Verified || v.ChangedCount == 0)
    {
      return null;
    }

    string? newest = v.NewestChangeUtc?.ToString("u", System.Globalization.CultureInfo.InvariantCulture);
    return "[verification gate] This turn changed " + v.ChangedCount
        + " file(s) but no command has exited 0 since the newest change (" + newest + ")."
        + " Note for your completion claim: back the claim with fresh evidence - any command that"
        + " verifies this work, started after the newest change, exit code 0. This is a note, not a"
        + " stop instruction; keep working.";
  }
}
