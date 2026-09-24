namespace eThangAgent.ToolDomain.Verification;

/// <summary>The verdict of one gate evaluation: whether the changed files are
///     covered by a fresh verification run, with the counts the refusal text
///     carries.</summary>
public readonly record struct VerificationVerdict(
    bool Verified,
    int ChangedCount,
    DateTimeOffset? NewestChangeUtc,
    string? LastVerificationSummary);

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
      return new VerificationVerdict(true, changedFiles.Count, null, null);
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

    return new VerificationVerdict(fresh, changedFiles.Count, newest, summary);
  }

  /// <summary>The turn gate's nudge contract, verbatim: null when verified or
  ///     nothing changed; otherwise the line the handler appends as a System
  ///     message (one per turn, never a loop).</summary>
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

    string lastVerification = v.LastVerificationSummary ?? "none";
    return "[verification gate] This turn changed " + v.ChangedCount + " file(s) but no verification-class command has run successfully since the newest change ("
        + v.NewestChangeUtc?.ToString("u", System.Globalization.CultureInfo.InvariantCulture)
        + "). Before claiming any completion: run the verification command for these changes, confirm exit code 0, and state the claim with that evidence. Last verification: "
        + lastVerification + ".";
  }
}
