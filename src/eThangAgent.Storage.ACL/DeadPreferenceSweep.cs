using System.Globalization;
using System.Text.Json.Nodes;
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

      // MEMBER-LEVEL sweep for a payload whose CONTAINER survives (routing removal):
      // the row stays because it still carries live sections, so the dead member is
      // stripped from the JSON in place and the key is reported as '<key>#routing'.
      await StripDeadMembersAsync(connection, transaction, removed, ct).ConfigureAwait(false);

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

  /// <summary>The payload MEMBERS whose feature is deleted, keyed by the preference-key
  ///     prefix whose JSON carries them.
  ///     <para>THIS IS NOT A KEY DELETE: the container row SURVIVES because it still carries
  ///     live sections (server_tools, plugins, reasoning_summary), so a whole-key sweep would
  ///     destroy settings the user still wants. Only the named member is stripped.</para>
  ///     <para><c>provider_settings:*#routing</c> - OpenRouter provider routing was DELETED
  ///     (the user now picks one model/provider pair instead of configuring routing). A stored
  ///     payload still carrying <c>routing</c> is not merely dead weight: it was the OTHER
  ///     writer of the request's <c>provider</c> key, so leaving it would describe a request
  ///     the harness can no longer make.</para></summary>
  public static IReadOnlyDictionary<string, IReadOnlyList<string>> DeadMembersByKeyPrefix { get; } =
      new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
      {
        ["provider_settings:"] = ["routing"],
      };

  /// <summary>Strips every dead member from the JSON payload of each matching preference row,
  ///     reporting each strip as <c>&lt;key&gt;#&lt;member&gt;</c>. A row whose JSON is
  ///     unparseable, or whose payload is not an object, is LEFT ALONE and reported - corrupt
  ///     storage is an infrastructure fault, and a cleanup that rewrote it would destroy
  ///     evidence a human needs.</summary>
  private static async Task StripDeadMembersAsync(SqliteConnection connection,
      SqliteTransaction transaction, List<string> removed, CancellationToken ct)
  {
    foreach ((string prefix, IReadOnlyList<string> members) in DeadMembersByKeyPrefix)
    {
      List<(string Key, string Json)> rows = [];
      using (SqliteCommand select = connection.CreateCommand())
      {
        select.Transaction = transaction;
        select.CommandText = "SELECT key, value FROM app_preferences WHERE key LIKE $value ESCAPE '\\' ORDER BY key;";
        _ = select.Parameters.AddWithValue("$value", EscapeLike(prefix) + "%");
        using SqliteDataReader reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
          rows.Add((reader.GetString(0), reader.GetString(1)));
        }
      }

      foreach ((string key, string json) in rows)
      {
        if (StripMembers(json, members) is not { } stripped)
        {
          continue;
        }

        using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE app_preferences SET value = $value, updated_at = $at WHERE key = $key;";
        _ = update.Parameters.AddWithValue("$value", stripped.Json);
        _ = update.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        _ = update.Parameters.AddWithValue("$key", key);
        _ = await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        removed.AddRange(stripped.RemovedMembers.Select(member => key + "#" + member));
      }
    }
  }

  /// <summary>Removes the named members from one JSON object payload. Returns null when
  ///     nothing was removed (so a clean row is never rewritten), or the stripped JSON plus
  ///     the member names actually removed. Unparseable JSON or a non-object payload returns
  ///     null and leaves the row untouched.</summary>
  private static (string Json, IReadOnlyList<string> RemovedMembers)? StripMembers(
      string json, IReadOnlyList<string> members)
  {
    JsonNode? node;
    try
    {
      node = JsonNode.Parse(json);
    }
    // Named decision (CA1031): a cleanup must not crash startup on corrupt storage; the
    // unparseable row is simply left as-is for a human to inspect.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception)
#pragma warning restore CA1031
    {
      return null;
    }

    if (node is not JsonObject payload)
    {
      return null;
    }

    // Remove() returns true only when the member existed, so it doubles as the filter:
    // the list ends up holding exactly the members this payload actually carried.
    List<string> removedMembers = [];
    foreach (string member in members.Where(payload.Remove))
    {
      removedMembers.Add(member);
    }

    return removedMembers.Count == 0 ? null : (payload.ToJsonString(), removedMembers);
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
