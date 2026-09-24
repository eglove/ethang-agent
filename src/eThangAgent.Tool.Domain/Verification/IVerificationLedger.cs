namespace eThangAgent.ToolDomain.Verification;

/// <summary>Append-only record of external commands this session has run.
///     One ledger per session container; freshness is only meaningful inside
///     one session, so nothing here persists.</summary>
public interface IVerificationLedger
{
  /// <summary>Reports one completed run. Implementations must never throw.</summary>
  void Append(ShellExecutionRecord record);

  /// <summary>A defensive copy of every recorded run, in append order.</summary>
  IReadOnlyList<ShellExecutionRecord> Snapshot();
}
