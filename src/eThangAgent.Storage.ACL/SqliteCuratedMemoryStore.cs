using System.Globalization;
using System.Text.Json;
using eThangAgent.MemoryDomain;
using eThangAgent.SharedKernel;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;


/// <summary>SQLite persistence for curated memories: a workspace-scoped table with
/// an external-content FTS5 index (kept in sync by triggers) for full-text search,
/// and compare-and-swap updates via version-predicated writes.</summary>
public sealed class SqliteCuratedMemoryStore(AppDatabase database) : ICuratedMemoryStore
{
  private readonly AppDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

  public async Task<Result<CuratedMemory>> AddAsync(CuratedMemory memory, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(memory);
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = """
                INSERT INTO curated_memories
                    (id, workspace_id, category, tags, content, usage_hint, scope, provenance, version, created_at, updated_at)
                VALUES (@id, @ws, @cat, @tags, @content, @hint, @scope, @prov, @version, @created, @updated);
                """;
      Fill(command, memory);
      _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      return Result.Success<CuratedMemory>(memory);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      return Result.Failure<CuratedMemory>(new DomainError(CuratedMemoryErrors.StorageError, ex.Message));
    }
  }

  public async Task<Result<CuratedMemory?>> GetAsync(Guid id, CancellationToken ct = default)
  {
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = $"""{SelectColumns} FROM curated_memories AS m WHERE m.id=@id;""";
      Add(command, "@id", id.ToString());
      using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
      return Result.Success<CuratedMemory?>(
          await reader.ReadAsync(ct).ConfigureAwait(false) ? MapRow(reader) : null);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      return Result.Failure<CuratedMemory?>(new DomainError(CuratedMemoryErrors.StorageError, ex.Message));
    }
  }

  public async Task<Result<IReadOnlyList<CuratedMemory>>> SearchAsync(
      string? workspaceId, string? query, MemoryCategory? category,
      IReadOnlyList<string>? tags, int limit, CancellationToken ct = default)
  {
    try
    {
      // Filters are predicates on the source table so they apply identically
      // in FTS mode and recency mode; visibility never depends on the mode.
      // Query mode joins the external-content index back to the source table,
      // which is also what gives bm25() a MATCH context to rank within.
      List<string> predicates =
      [
                "(m.scope='global' OR (@ws IS NOT NULL AND m.workspace_id=@ws))",
            ];
      List<SqliteParameter> parameters = [Param("@ws", (object?)workspaceId ?? DBNull.Value)];
      if (category.HasValue)
      {
        predicates.Add("m.category=@cat");
        // Named decision (CA1308): stored categories are lowercased by design; uppercasing
        // would break every persisted row.
#pragma warning disable CA1308 // Normalize strings to uppercase
        parameters.Add(Param("@cat", category.Value.ToString().ToLowerInvariant()));
#pragma warning restore CA1308 // Normalize strings to uppercase
      }
      foreach (string tag in tags ?? [])
      {
        string name = $"@tag{parameters.Count}";
        // The pattern is quote-bounded to JSON element boundaries: System.Text.Json
        // always quotes array elements, so %"tag"% can never substring-match inside
        // a longer element ("sql" no longer hits a row tagged ["mysql"]).
        predicates.Add($"m.tags LIKE {name} ESCAPE '\\'");
        parameters.Add(Param(name, $"%\"{TagPattern(tag)}\"%"));
      }

      string sql;
      if (!string.IsNullOrWhiteSpace(query))
      {
        predicates.Add("curated_memories_fts MATCH @match");
        parameters.Add(Param("@match", BuildMatchExpression(query)));
        sql = $"""
                    {SelectColumns}
                    FROM curated_memories_fts
                    JOIN curated_memories AS m ON m.rowid = curated_memories_fts.rowid
                    WHERE {string.Join(" AND ", predicates)}
                    ORDER BY bm25(curated_memories_fts), m.updated_at DESC
                    LIMIT @limit;
                    """;
      }
      else
      {
        sql = $"{SelectColumns} FROM curated_memories AS m WHERE {string.Join(" AND ", predicates)} ORDER BY m.updated_at DESC, m.id LIMIT @limit;";
      }

      parameters.Add(Param("@limit", limit));

      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      using SqliteCommand command = connection.CreateCommand();
      // Named decision (CA2100): predicates are fixed strings joined with AND; all values
#pragma warning disable CA2100 // Review SQL query for security vulnerabilities
      command.CommandText = sql;
#pragma warning restore CA2100 // Review SQL query for security vulnerabilities
      foreach (SqliteParameter parameter in parameters)
      {
        _ = command.Parameters.Add(parameter);
      }

      List<CuratedMemory> memories = [];
      using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
      while (await reader.ReadAsync(ct).ConfigureAwait(false))
      {
        memories.Add(MapRow(reader));
      }

      return Result.Success<IReadOnlyList<CuratedMemory>>(memories);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      return Result.Failure<IReadOnlyList<CuratedMemory>>(new DomainError(CuratedMemoryErrors.StorageError, ex.Message));
    }
  }

  public async Task<Result<CuratedMemory>> UpdateAsync(CuratedMemory updated, CancellationToken ct = default)
  {
    try
    {
      // One transaction spans the CAS write and its disambiguation: without it a
      // concurrent delete between the two statements would misreport a version
      // conflict as absence.
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
#pragma warning restore CA2007
      int changed;
      using (SqliteCommand command = connection.CreateCommand())
      {
        command.Transaction = transaction;
        command.CommandText = """
                    UPDATE curated_memories
                    SET workspace_id=@ws, category=@cat, tags=@tags, content=@content, usage_hint=@hint,
                        scope=@scope, provenance=@prov, version=@version, created_at=@created, updated_at=@updated
                    WHERE id=@id AND version=@expected;
                    """;
        ArgumentNullException.ThrowIfNull(updated);
        Fill(command, updated);
        Add(command, "@expected", updated.Version - 1);
        changed = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      }

      if (changed > 0)
      {
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return Result.Success<CuratedMemory>(updated);
      }

      // Disambiguate: a stale version conflicts with the stored row; an unknown id is absence.
      string code;
      string message;
      using (SqliteCommand current = connection.CreateCommand())
      {
        current.Transaction = transaction;
        current.CommandText = "SELECT version FROM curated_memories WHERE id=@id;";
        ArgumentNullException.ThrowIfNull(updated);
        Add(current, "@id", updated.Id.ToString());
        object? storedVersion = await current.ExecuteScalarAsync(ct).ConfigureAwait(false);
        (code, message) = storedVersion is null
            ? (CuratedMemoryErrors.MemoryNotFound,
                $"No curated memory with id '{updated.Id}' exists.")
            : (CuratedMemoryErrors.VersionConflict,
                // Same phrasing as the provider's pre-check so the model sees one
                // consistent conflict message whichever layer catches the staleness.
                $"current stored version is {Convert.ToInt32(storedVersion, CultureInfo.InvariantCulture)}.");
      }

      await transaction.CommitAsync(ct).ConfigureAwait(false);
      return Result.Failure<CuratedMemory>(new DomainError(code, message));
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      return Result.Failure<CuratedMemory>(new DomainError(CuratedMemoryErrors.StorageError, ex.Message));
    }
  }

  public async Task<Result<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
  {
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = "DELETE FROM curated_memories WHERE id=@id;";
      Add(command, "@id", id.ToString());
      int deleted = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      return Result.Success<bool>(deleted > 0);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      return Result.Failure<bool>(new DomainError(CuratedMemoryErrors.StorageError, ex.Message));
    }
  }

  private const string SelectColumns = """
        SELECT m.id, m.workspace_id, m.category, m.tags, m.content, m.usage_hint, m.scope, m.provenance, m.version, m.created_at, m.updated_at
        """;

  /// <summary>
  /// Builds the FTS5 MATCH expression for a free-text query: whitespace-split
  /// tokens, each double-quoted (embedded quotes doubled) so FTS punctuation
  /// syntax in user input can never alter the query's shape.
  /// </summary>
  private static string BuildMatchExpression(string query)
      => string.Join(' ', query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
          .Select(token => $"\"{token.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));

  /// <summary>Escapes LIKE wildcards so a validated tag matches only itself.</summary>
  private static string TagPattern(string tag)
      => tag.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal);

  private static SqliteParameter Param(string name, object value)
      => new(name, value);

  // Named decision (CA1308): categories/scopes are persisted lowercased; uppercasing
#pragma warning disable CA1308 // Normalize strings to uppercase
  // would break every stored row.
  private static void Fill(SqliteCommand command, CuratedMemory memory)
  {
    Add(command, "@id", memory.Id.ToString());
    Add(command, "@ws", memory.WorkspaceId);
    Add(command, "@cat", memory.Category.ToString().ToLowerInvariant());
    Add(command, "@tags", JsonSerializer.Serialize(memory.Tags));
    Add(command, "@content", memory.Content);
    Add(command, "@hint", (object?)memory.UsageHint ?? DBNull.Value);
    Add(command, "@scope", memory.Scope.ToString().ToLowerInvariant());
    Add(command, "@prov", (object?)memory.ProvenanceSession ?? DBNull.Value);
    Add(command, "@version", memory.Version);
    Add(command, "@created", memory.CreatedAt.ToString("o"));
    Add(command, "@updated", memory.UpdatedAt.ToString("o"));
  }

  private static CuratedMemory MapRow(SqliteDataReader reader)
  {
    Result<MemoryCategory> categoryResult = CuratedMemorySpecifications.ParseCategory(reader.GetString(2));
    if (!categoryResult.IsSuccess)
    {
      throw new InvalidOperationException(
          $"Stored curated memory '{reader.GetString(0)}' has an unreadable category.", null);
    }

    Result<MemoryScope> scopeResult = CuratedMemorySpecifications.ParseScope(reader.GetString(6));
    return !scopeResult.IsSuccess
      ? throw new InvalidOperationException(
          $"Stored curated memory '{reader.GetString(0)}' has an unreadable scope.", null)
      : new CuratedMemory(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        categoryResult.Value,
        JsonSerializer.Deserialize<List<string>>(reader.GetString(3)) ?? [],
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        scopeResult.Value,
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.GetInt32(8),
        DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture));
  }

  private static void Add(SqliteCommand command, string name, object value)
      => command.Parameters.AddWithValue(name, value);
}
#pragma warning restore CA1308 // Normalize strings to uppercase
