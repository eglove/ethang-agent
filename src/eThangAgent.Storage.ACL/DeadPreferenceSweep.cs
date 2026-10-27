using System.Globalization;
using eThangAgent.SharedKernel;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>One-time startup sweep for preference rows whose feature was deleted (issue #298).
///     <para>Dead preferences are key-value strings with no schema, so they cannot be removed by
///     a migration the way a table can: the sweep deletes the rows whose keys match a named
///     prefix and reports what it removed. It runs on every app start rather than once, because
///     a delete leaves no marker to remember — the second run simply finds nothing, which is the
///     cheapest correct answer and needs no extra state.</para>
///     <para>PREFIXES ARE NAMED EXPLICITLY (D3): a pattern like <c>%selection%</c> would
///     eventually eat a live key. Every entry here is a key whose reader and writer are both
///     deleted, so nothing can recreate it.</para></summary>
public static class DeadPreferenceSweep
{
  /// <summary>Key PREFIXES whose feature is deleted. Every entry ends with its own
  ///     separator (<c>:</c>), so a prefix can never swallow a longer sibling key that merely
  ///     starts with the same text — <c>smart_selection_notes</c> survives <c>smart_selection:</c>
  ///     because the separator is part of the match. A key is added here only when the code
  ///     that read and wrote it is gone.
  ///     <para><c>smart_selection:</c> — the smart-selection feature's per-workspace constraints
  ///     (<c>smart_selection:{provider}:{workspaceRoot}</c>). The selector itself
  ///     (<c>IModelSelector</c>, <c>SmartModelSelector</c>, <c>MeasuredMixPolicy</c>) is deleted;
  ///     zero references remain in <c>src/</c>.</para></summary>
  public static IReadOnlyList<string> DeadKeyPrefixes { get; } = ["smart_selection:"];

  /// <summary>EXACT keys whose feature is deleted. An exact key cannot over-match by
  ///     construction, which is why the retired remote-host toggle is named here rather than
  ///     as a prefix: <c>subagent_remote_host</c> as a prefix would also eat a hypothetical
  ///     <c>subagent_remote_host_*</c> sibling, and the toggle's key has no suffix to anchor
  ///     on. Every conversation runs in its own child host unconditionally (spec D6/D7), so
  ///     the toggle and its key are deleted; zero references remain in <c>src/</c>.</summary>
  public static IReadOnlyList<string> DeadKeys { get; } = ["subagent_remote_host"];

  /// <summary>Deletes every preference row whose key starts with a dead prefix. Returns the
  ///     removed keys in ordinal order so the caller can log them; an empty result means there
  ///     was nothing to remove. A storage failure is reported as a Result failure rather than
  ///     thrown: a cleanup that cannot run must never stop the app from starting.</summary>
  public static async Task<Result<IReadOnlyList<string>>> SweepAsync(
      AppDatabase database, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(database);

    try
    {
#pragma warning disable CA2007
      await using SqliteConnection connection = database.Open();
#pragma warning restore CA2007
      List<string> removed = [];
#pragma warning disable CA2007
      await using SqliteTransaction transaction =
          (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
#pragma warning restore CA2007
      // Prefixes match by PREFIX; exact keys match by EQUALITY. Both ride PARAMETERS (never
      // interpolation) and LIKE's own wildcards are escaped, so a prefix containing '%' or
      // '_' matches literally - the whole point of naming prefixes is that nothing
      // unintended is eaten.
      foreach (string prefix in DeadKeyPrefixes)
      {
        await CollectAsync(
            static command => command.CommandText =
                "SELECT key FROM app_preferences WHERE key LIKE $value ESCAPE '\\' ORDER BY key;",
            EscapeLike(prefix) + "%").ConfigureAwait(false);
      }
      foreach (string key in DeadKeys)
      {
        await CollectAsync(
            static command => command.CommandText =
                "SELECT key FROM app_preferences WHERE key = $value ORDER BY key;",
            key).ConfigureAwait(false);
      }

      async Task CollectAsync(Action<SqliteCommand> setSql, string value)
      {
        using SqliteCommand select = connection.CreateCommand();
        select.Transaction = transaction;
        setSql(select);
        _ = select.Parameters.AddWithValue("$value", value);
        using SqliteDataReader reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
          removed.Add(reader.GetString(0));
        }
        await reader.CloseAsync().ConfigureAwait(false);
      }

      foreach (string key in removed)
      {
        using SqliteCommand delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM app_preferences WHERE key = $key;";
        _ = delete.Parameters.AddWithValue("$key", key);
        _ = await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      }

      await transaction.CommitAsync(ct).ConfigureAwait(false);
      return Result.Success<IReadOnlyList<string>>(removed);
    }
    // Named decision (CA1031): the sweep is a startup cleanup, not a feature. A locked or
    // unreadable database must not stop the app from opening - it reports the failure.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception ex)
#pragma warning restore CA1031
    {
      return Result.Failure<IReadOnlyList<string>>(
          new DomainError("DeadPreferenceSweepFailed", ex.Message));
    }
  }

  /// <summary>The one-line log message for a completed sweep: silent when nothing was
  ///     removed (a startup line about nothing is noise), and otherwise names every key so
  ///     the removal is auditable after the fact.</summary>
  public static string? DescribeOutcome(IReadOnlyList<string> removed)
  {
    ArgumentNullException.ThrowIfNull(removed);
    return removed.Count == 0
        ? null
        : string.Format(CultureInfo.InvariantCulture,
            "dead preference sweep removed {0} row(s): {1}",
            removed.Count,
            string.Join(", ", removed));
  }

  /// <summary>Escapes LIKE's own wildcards so a named prefix matches literally.</summary>
  private static string EscapeLike(string value) => value
      .Replace("\\", "\\\\", StringComparison.Ordinal)
      .Replace("%", "\\%", StringComparison.Ordinal)
      .Replace("_", "\\_", StringComparison.Ordinal);
}
