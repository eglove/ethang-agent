using System.Text.Json;
using eThangAgent.MemoryDomain;
using eThangAgent.SharedKernel;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>SQLite persistence for curated memories: a workspace-scoped table with
/// an external-content FTS5 index (kept in sync by triggers) for full-text search,
/// and compare-and-swap updates via version-predicated writes.</summary>
public sealed class SqliteCuratedMemoryStore : ICuratedMemoryStore
{
    private readonly AppDatabase _database;

    public SqliteCuratedMemoryStore(AppDatabase database)
        => _database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task<Result<CuratedMemory>> AddAsync(CuratedMemory memory, CancellationToken ct = default)
    {
        try
        {
            await using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO curated_memories
                    (id, workspace_id, category, tags, content, usage_hint, scope, provenance, version, created_at, updated_at)
                VALUES (@id, @ws, @cat, @tags, @content, @hint, @scope, @prov, @version, @created, @updated);
                """;
            Fill(command, memory);
            await command.ExecuteNonQueryAsync(ct);
            return Result<CuratedMemory>.Success(memory);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<CuratedMemory>.Failure(new Error(CuratedMemoryErrors.StorageError, ex.Message));
        }
    }

    public async Task<Result<CuratedMemory?>> GetAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            await using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"""{SelectColumns} FROM curated_memories AS m WHERE m.id=@id;""";
            Add(command, "@id", id.ToString());
            using var reader = await command.ExecuteReaderAsync(ct);
            return Result<CuratedMemory?>.Success(
                await reader.ReadAsync(ct) ? MapRow(reader) : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<CuratedMemory?>.Failure(new Error(CuratedMemoryErrors.StorageError, ex.Message));
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
            var predicates = new List<string>
            {
                "(m.scope='global' OR (@ws IS NOT NULL AND m.workspace_id=@ws))",
            };
            var parameters = new List<SqliteParameter> { Param("@ws", (object?)workspaceId ?? DBNull.Value) };
            if (category.HasValue)
            {
                predicates.Add("m.category=@cat");
                parameters.Add(Param("@cat", category.Value.ToString().ToLowerInvariant()));
            }
            foreach (var tag in tags ?? [])
            {
                var name = $"@tag{parameters.Count}";
                predicates.Add($"m.tags LIKE {name} ESCAPE '\\'");
                parameters.Add(Param(name, $"%{TagPattern(tag)}%"));
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
                sql = $"{SelectColumns} FROM curated_memories AS m WHERE {string.Join(" AND ", predicates)} ORDER BY m.updated_at DESC LIMIT @limit;";
            }

            parameters.Add(Param("@limit", limit));

            await using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters)
                command.Parameters.Add(parameter);

            var memories = new List<CuratedMemory>();
            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                memories.Add(MapRow(reader));
            return Result<IReadOnlyList<CuratedMemory>>.Success(memories);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<IReadOnlyList<CuratedMemory>>.Failure(new Error(CuratedMemoryErrors.StorageError, ex.Message));
        }
    }

    public async Task<Result<CuratedMemory>> UpdateAsync(CuratedMemory updated, CancellationToken ct = default)
    {
        try
        {
            await using var connection = _database.Open();
            int changed;
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE curated_memories
                    SET workspace_id=@ws, category=@cat, tags=@tags, content=@content, usage_hint=@hint,
                        scope=@scope, provenance=@prov, version=@version, created_at=@created, updated_at=@updated
                    WHERE id=@id AND version=@expected;
                    """;
                Fill(command, updated);
                Add(command, "@expected", updated.Version - 1);
                changed = await command.ExecuteNonQueryAsync(ct);
            }

            if (changed > 0)
                return Result<CuratedMemory>.Success(updated);

            // Disambiguate: a stale version conflicts with the stored row; an unknown id is absence.
            using (var current = connection.CreateCommand())
            {
                current.CommandText = "SELECT version FROM curated_memories WHERE id=@id;";
                Add(current, "@id", updated.Id.ToString());
                var storedVersion = await current.ExecuteScalarAsync(ct);
                return storedVersion is null
                    ? Result<CuratedMemory>.Failure(new Error(
                        CuratedMemoryErrors.MemoryNotFound,
                        $"No curated memory with id '{updated.Id}' exists."))
                    : Result<CuratedMemory>.Failure(new Error(
                        CuratedMemoryErrors.VersionConflict,
                        $"Expected version {updated.Version - 1} but the stored version is {Convert.ToInt32(storedVersion)}."));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<CuratedMemory>.Failure(new Error(CuratedMemoryErrors.StorageError, ex.Message));
        }
    }

    public async Task<Result<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            await using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM curated_memories WHERE id=@id;";
            Add(command, "@id", id.ToString());
            var deleted = await command.ExecuteNonQueryAsync(ct);
            return Result<bool>.Success(deleted > 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<bool>.Failure(new Error(CuratedMemoryErrors.StorageError, ex.Message));
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
            .Select(token => $"\"{token.Replace("\"", "\"\"")}\""));

    /// <summary>Escapes LIKE wildcards so a validated tag matches only itself.</summary>
    private static string TagPattern(string tag)
        => tag.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    private static SqliteParameter Param(string name, object value)
        => new(name, value);

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
        var categoryResult = CuratedMemorySpecifications.ParseCategory(reader.GetString(2));
        if (!categoryResult.IsSuccess)
            throw new InvalidOperationException(
                $"Stored curated memory '{reader.GetString(0)}' has an unreadable category.", null);
        var scopeResult = CuratedMemorySpecifications.ParseScope(reader.GetString(6));
        if (!scopeResult.IsSuccess)
            throw new InvalidOperationException(
                $"Stored curated memory '{reader.GetString(0)}' has an unreadable scope.", null);

        return new CuratedMemory(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            categoryResult.Value,
            JsonSerializer.Deserialize<List<string>>(reader.GetString(3)) ?? [],
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            scopeResult.Value,
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetInt32(8),
            DateTimeOffset.Parse(reader.GetString(9)),
            DateTimeOffset.Parse(reader.GetString(10)));
    }

    private static void Add(SqliteCommand command, string name, object value)
        => command.Parameters.AddWithValue(name, value);
}
