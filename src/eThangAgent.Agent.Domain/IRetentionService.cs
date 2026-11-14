using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>What one retention run deleted, by table, and what the file became.
///     <para>THE TABLE COUNTS ARE PART OF THE CONTRACT, not a diagnostic: the sweep's table list
///     is fixed and named, and a run that reports which tables it touched is the account a reader
///     needs to see that nothing else was touched (issue #246).</para></summary>
/// <param name="Window">The window the run applied.</param>
/// <param name="Cutoff">The instant the run treated as 'older than'.</param>
/// <param name="Deleted">Rows deleted, by table.</param>
/// <param name="FileBytesBefore">The database's own accounting of its size before the run.</param>
/// <param name="FileBytesAfter">Its size after the run, which is the number a reclaim moves.</param>
/// <param name="FreePagesAfter">Free pages the file holds afterwards; zero means reclaimed.</param>
/// <param name="ReclaimFailure">Why the reclaim did not finish, or null.</param>
/// <param name="CompletedAt">When the run finished.</param>
public sealed record RetentionRun(
    TimeSpan Window,
    DateTimeOffset Cutoff,
    IReadOnlyDictionary<string, int> Deleted,
    long FileBytesBefore,
    long FileBytesAfter,
    long FreePagesAfter,
    string? ReclaimFailure,
    DateTimeOffset CompletedAt)
{
  /// <summary>Every row this run deleted, across every table.</summary>
  public int RowsDeleted => Deleted.Values.Sum();

  /// <summary>How much smaller the file became, which is the outcome the run exists for.</summary>
  public long BytesReclaimed => FileBytesBefore - FileBytesAfter;
}

/// <summary>The retention sweep as the app asks for it (issue #246): every app-level mechanism
///     reads this port, so the host runs one sweep a day without knowing which table is oldest.
///     <para>THE WINDOW IS THE IMPLEMENTATION'S BUSINESS, and so is disabling: a run answers null
///     when retention is switched off, and the caller has nothing to decide.</para></summary>
public interface IRetentionService
{
  /// <summary>Deletes rows older than the configured window and reclaims the file.
  ///     <para>NULL MEANS RETENTION IS DISABLED, which is a different fact from a run that found
  ///     nothing: the first is a setting, the second is an outcome.</para></summary>
  /// <param name="ct">Cancellation for the run.</param>
  Task<Result<RetentionRun?>> RunOnceAsync(CancellationToken ct = default);
}
