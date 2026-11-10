namespace eThangAgent.ToolDomain.Lsp;

/// <summary>The advisory diagnostics seam (issue #356): the Tool Domain's provider-neutral
/// view over language-server diagnostics appended to successful edit/write results. The
/// ACL owns the wire (pipe query to the daemon); the domain never learns transports exist.
/// Expected failures flow as typed result values with explicit honesty labels. The seam
/// never throws domain errors and never fails an edit: a slow or absent server yields a
/// labeled result, never an exception.</summary>
public interface IAdvisoryDiagnosticsSource
{
  Task<AdvisoryDiagnosticsResult> GetAsync(string path, CancellationToken ct = default);
}

/// <summary>Honesty label for an advisory diagnostics query, matching the diagnostics
/// slice (#354): Ready carries evidence; Starting/Stale/Degraded/Unavailable are labeled
/// degraded states, never silence and never clean.</summary>
public enum AdvisoryDiagnosticsState
{
  Ready,
  Starting,
  Stale,
  Degraded,
  Unavailable,
}

/// <summary>One diagnostic with its 1-based line range, code, and message.</summary>
public sealed record AdvisoryDiagnostic(
    int StartLine,
    int StartCharacter,
    int EndLine,
    int EndCharacter,
    string? Code,
    string Message);

/// <summary>Result of an advisory diagnostics query: explicit state plus diagnostics.
/// Empty is never clean: a file with no diagnostics yet reports Starting, never Ready
/// with zero items unless the server has actually reported for it. <c>LanguageId</c> names
/// the language whose server produced the report, or null when no server was reached.</summary>
public sealed record AdvisoryDiagnosticsResult(
    AdvisoryDiagnosticsState State,
    IReadOnlyList<AdvisoryDiagnostic> Diagnostics,
    string? Reason = null,
    string? LanguageId = null);

/// <summary>One recorded advisory lookup: the file, its honesty label, and the
/// diagnostics scoped to the changed lines at query time.</summary>
public sealed record AdvisoryDiagnosticsEntry(
    string Path,
    AdvisoryDiagnosticsState State,
    IReadOnlyList<AdvisoryDiagnostic> Diagnostics);

/// <summary>Session-local advisory diagnostics ledger (issue #356), separate from
/// <c>SessionVerificationLedger</c>. Same lifetime as the session container, never
/// persisted, no history surface beyond the in-memory snapshot.</summary>
public sealed class SessionAdvisoryDiagnosticsLedger
{
  private readonly Lock _gate = new();
  private readonly List<AdvisoryDiagnosticsEntry> _entries = [];

  /// <summary>Records one advisory lookup. Never throws by contract: a ledger fault
  /// must never break the edit/write being reported.</summary>
  public void Append(AdvisoryDiagnosticsEntry entry)
  {
    ArgumentNullException.ThrowIfNull(entry);
    try
    {
      lock (_gate)
      {
        _entries.Add(entry);
      }
    }
    // Named decision (CA1031): Append never throws (spec error rule) - a ledger
    // fault must never break the edit/write being reported.
#pragma warning disable CA1031 // Do not catch general exception types
    catch
    {
      // Named decision (CA1031, S108): Append never throws - a ledger fault
      // must never break the edit/write being reported. Swallowing IS the contract.
    }
#pragma warning restore CA1031 // Do not catch general exception types
  }

  /// <summary>A defensive copy of every recorded lookup, in append order.</summary>
  public IReadOnlyList<AdvisoryDiagnosticsEntry> Snapshot()
  {
    lock (_gate)
    {
      return [.. _entries];
    }
  }
}
