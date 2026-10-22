namespace eThangAgent.ToolDomain.Verification;

/// <summary>The verdict of one gate evaluation: whether the changed files are
///     covered by a fresh verification run, with the counts the warning text
///     carries and whether any verification-class success is known to the
///     ledger at all (the turn gate's no-known-command escape, issue #256).</summary>
public readonly record struct VerificationVerdict(
    bool Verified,
    int ChangedCount,
    DateTimeOffset? NewestChangeUtc,
    string? LastVerificationSummary,
    bool KnownVerificationCommand);

/// <summary>Shared front for both gates (commit and turn): evaluates the
///     changed-file set against the session ledger. A disabled gate verifies
///     everything.</summary>
public sealed class VerificationGate(
    IVerificationLedger ledger,
    VerificationFreshnessSpecification freshness,
    bool enabled)
{
  private readonly IVerificationLedger _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
  private readonly VerificationFreshnessSpecification _freshness =
      freshness ?? throw new ArgumentNullException(nameof(freshness));

  /// <summary>Configuration switch: false means byte-identical legacy behavior.</summary>
  public bool Enabled { get; } = enabled;

  /// <summary>Evaluates the changed files (already resolved by the caller) against
  ///     the ledger. Nothing changed always verifies.</summary>
  public VerificationVerdict Evaluate(IReadOnlyList<(string Path, DateTimeOffset ModifiedUtc)> changedFiles)
  {
    ArgumentNullException.ThrowIfNull(changedFiles);
    if (!Enabled)
    {
      // KnownVerificationCommand is meaningless for a disabled gate: it renders no
      // text, and scanning the ledger here would defeat the disabled gate's
      // zero-work contract.
      return new VerificationVerdict(true, changedFiles.Count, null, null, KnownVerificationCommand: false);
    }

    DateTimeOffset? newest = 0 < changedFiles.Count ? changedFiles.Max(c => c.ModifiedUtc) : null;
    IReadOnlyList<ShellExecutionRecord> snapshot = _ledger.Snapshot();
    bool fresh = _freshness.IsSatisfiedBy(snapshot, changedFiles);
    string? summary = null;
    ShellExecutionRecord? freshest = snapshot
        .Where(r => r.ExitCode == 0 && (newest is null || r.StartedUtc > newest))
        .OrderByDescending(r => r.StartedUtc)
        .FirstOrDefault();
    if (freshest is not null)
    {
      summary = string.Join(' ', freshest.Tokens) + " at " + freshest.StartedUtc.ToString("u", System.Globalization.CultureInfo.InvariantCulture);
    }

    bool knownCommand = _freshness.HasKnownCommand(snapshot);
    return new VerificationVerdict(fresh, changedFiles.Count, newest, summary, knownCommand);
  }

  /// <summary>The commit gate's warning contract: the commit proceeds; this
  ///     line appends to its output when changed files lack a fresh
  ///     verification run. Callers invoke it only on an unverified verdict.</summary>
  public static string WarningLine(VerificationVerdict v)
  {
    string lastVerification = v.LastVerificationSummary ?? "none";
    string commands = string.Join(", ", VerificationCommandSpecificationDefaults.Commands);
    return "[warning] verification gate: " + v.ChangedCount + " file(s) changed; newest change "
        + v.NewestChangeUtc?.ToString("u", System.Globalization.CultureInfo.InvariantCulture)
        + "; last verification: " + lastVerification
        + ". No successful verification-class command started after the newest change (default set: "
        + commands + "); run one before claiming completion.";
  }

  /// <summary>The turn gate's nudge contract, verbatim: null when verified or
  ///     nothing changed; otherwise an ADVISORY note the handler appends as a
  ///     System message (one per turn, never a loop). Issue #256: the note is
  ///     weighed when the model claims completion, never obeyed as a turn
  ///     precondition, and a ledger with no successful verification-class run at
  ///     all says so plainly instead of implying a command must exist.</summary>
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
    if (!v.KnownVerificationCommand)
    {
      // No verification-class run has EVER succeeded in this session: demanding
      // one would be un-satisfiable, so the note names the gap and licenses an
      // honest "nothing verified" claim instead (issue #256).
      return "[verification gate] This turn changed " + v.ChangedCount
          + " file(s) but no verification-class command has run successfully since the newest change ("
          + newest + "), and no verification command is known for this workspace."
          + " Note for your completion claim: if the workspace has a way to verify these changes,"
          + " run it and confirm exit code 0 before claiming completion; otherwise say plainly"
          + " that nothing verified. This is a note, not a stop instruction; keep working.";
    }

    // LastVerificationSummary is dead text here: it names only runs STARTED after
    // the newest change, and any such run would have made the verdict fresh, so
    // the nudge would not fire. The known/never distinction below is the honest
    // signal this line can carry.
    return "[verification gate] This turn changed " + v.ChangedCount
        + " file(s) but no verification-class command has run successfully since the newest change ("
        + newest + "). Note for your completion claim: back the claim with fresh verification"
        + " evidence - a successful verification-class command started after the newest change,"
        + " exit code 0. This is a note, not a stop instruction; keep working.";
  }
}
