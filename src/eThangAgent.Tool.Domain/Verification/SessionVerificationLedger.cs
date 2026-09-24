namespace eThangAgent.ToolDomain.Verification;

/// <summary>In-memory verification ledger, one per session container. A
///     system lock guards the append-only list; Snapshot hands out copies so
///     callers cannot corrupt the history.</summary>
public sealed class SessionVerificationLedger : IVerificationLedger
{
  private readonly Lock _gate = new();

  private readonly List<ShellExecutionRecord> _records = [];

  /// <summary>Reports one completed run. Never throws by contract: a ledger
  ///     fault must never break the command run being reported.</summary>
  public void Append(ShellExecutionRecord record)
  {
    try
    {
      lock (_gate)
      {
        _records.Add(record);
      }
    }
    // Named decision (CA1031): Append never throws (spec error rule) - a ledger
    // fault must never break the command run being reported.
#pragma warning disable CA1031 // Do not catch general exception types
    catch
    {
      // Named decision (CA1031, S108): Append never throws - a ledger fault
      // must never break the command run being reported. Swallowing IS the contract.
    }
#pragma warning restore CA1031 // Do not catch general exception types
  }

  /// <summary>A defensive copy of every recorded run, in append order.</summary>
  public IReadOnlyList<ShellExecutionRecord> Snapshot()
  {
    lock (_gate)
    {
      return [.. _records];
    }
  }
}
