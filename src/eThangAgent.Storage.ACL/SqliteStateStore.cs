using System.Text.Json;
using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

public sealed class SqliteStateStore : IStateStore
{
    private readonly AppDatabase _database;

    public SqliteStateStore(AppDatabase database)
        => _database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task<StateKeyValue?> GetKeyAsync(string workspaceId, string ns, string name,
        CancellationToken ct = default)
    {
        await using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value, version FROM state_keys WHERE workspace_id=@w AND ns=@ns AND name=@n;";
        Add(command, "@w", workspaceId);
        Add(command, "@ns", ns);
        Add(command, "@n", name);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new StateKeyValue(ns, name, reader.GetString(0), reader.GetInt32(1))
            : null;
    }

    public async Task<IReadOnlyList<StateKeyValue>> ListKeysAsync(string workspaceId, string? ns,
        CancellationToken ct = default)
    {
        await using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ns, name, value, version FROM state_keys WHERE workspace_id=@w (@nsFilter) ORDER BY ns, name;"
            .Replace("(@nsFilter)", ns is null ? "" : "AND ns=@ns");
        Add(command, "@w", workspaceId);
        if (ns is not null) Add(command, "@ns", ns);
        var keys = new List<StateKeyValue>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            keys.Add(new StateKeyValue(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        return keys;
    }

    public async Task<StateKeyValue?> SetKeyCasAsync(string workspaceId, string ns, string name,
        string value, int? expectedVersion, CancellationToken ct = default)
    {
        await using var connection = _database.Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow.ToString("o");

        if (expectedVersion.HasValue)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE state_keys SET value=@v, version=version+1, updated_at=@now
                WHERE workspace_id=@w AND ns=@ns AND name=@n AND version=@exp;
                """;
            Add(update, "@v", value);
            Add(update, "@w", workspaceId);
            Add(update, "@ns", ns);
            Add(update, "@n", name);
            Add(update, "@now", now);
            Add(update, "@exp", expectedVersion.Value);
            if (await update.ExecuteNonQueryAsync(ct) == 0)
            {
                await transaction.RollbackAsync(ct);
                return null;
            }
        }
        else
        {
            using var upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO state_keys (workspace_id, ns, name, value, version, updated_at)
                VALUES (@w, @ns, @n, @v, 1, @now)
                ON CONFLICT(workspace_id, ns, name) DO UPDATE SET
                    value=@v, version=state_keys.version+1, updated_at=@now;
                """;
            Add(upsert, "@w", workspaceId);
            Add(upsert, "@ns", ns);
            Add(upsert, "@n", name);
            Add(upsert, "@v", value);
            Add(upsert, "@now", now);
            await upsert.ExecuteNonQueryAsync(ct);
        }

        StateKeyValue? row;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT value, version FROM state_keys WHERE workspace_id=@w AND ns=@ns AND name=@n;";
            Add(select, "@w", workspaceId);
            Add(select, "@ns", ns);
            Add(select, "@n", name);
            using var reader = await select.ExecuteReaderAsync(ct);
            row = await reader.ReadAsync(ct)
                ? new StateKeyValue(ns, name, reader.GetString(0), reader.GetInt32(1))
                : null;
        }
        await transaction.CommitAsync(ct);
        return row;
    }

    public async Task<bool> DeleteKeyCasAsync(string workspaceId, string ns, string name,
        int? expectedVersion, CancellationToken ct = default)
    {
        await using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM state_keys WHERE workspace_id=@w AND ns=@ns AND name=@n AND (@exp IS NULL OR version=@exp);";
        Add(command, "@w", workspaceId);
        Add(command, "@ns", ns);
        Add(command, "@n", name);
        Add(command, "@exp", (object?)expectedVersion ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<Result<IReadOnlyList<StateSearchHit>>> SearchKeysAsync(
        string workspaceId, string query, int limit, CancellationToken ct = default)
    {
        await using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT k.ns, k.name, snippet(state_keys_fts, 0, '[', ']', '…', 12)
            FROM state_keys_fts f
            JOIN state_keys k ON k.rowid = f.rowid
            WHERE k.workspace_id = @w AND state_keys_fts MATCH @q
            ORDER BY rank
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@w", workspaceId);
        command.Parameters.AddWithValue("@q", query);
        command.Parameters.AddWithValue("@limit", limit);
        try
        {
            var hits = new List<StateSearchHit>();
            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                hits.Add(new StateSearchHit(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            return Result<IReadOnlyList<StateSearchHit>>.Success(hits);
        }
        catch (SqliteException ex)
        {
            return Result<IReadOnlyList<StateSearchHit>>.Failure(
                new Error("InvalidQuery", $"Full-text search rejected the query '{query}': {ex.Message}"));
        }
    }
    public async Task<TransitionRecord> InsertTransitionAsync(string workspaceId,
        TransitionRecord transition, CancellationToken ct = default)
    {
        await using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO transitions (id, workspace_id, from_state, to_state, summary, evidence_json, status, created_at)
            VALUES (@id, @w, @from, @to, @summary, @evidence, @status, @created);
            """;
        Add(command, "@id", transition.Id);
        Add(command, "@w", workspaceId);
        Add(command, "@from", transition.From);
        Add(command, "@to", transition.To);
        Add(command, "@summary", transition.Summary);
        Add(command, "@evidence", JsonSerializer.Serialize(transition.Evidence));
        Add(command, "@status", transition.Status);
        Add(command, "@created", transition.CreatedAt.ToString("o"));
        await command.ExecuteNonQueryAsync(ct);
        return transition;
    }

    public async Task<IReadOnlyList<TransitionRecord>> GetTransitionsAsync(string workspaceId,
        IReadOnlyList<string> transitionIds, CancellationToken ct = default)
    {
        await using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = transitionIds.Count == 0
            ? "SELECT id, from_state, to_state, summary, evidence_json, status, created_at FROM transitions WHERE workspace_id=@w AND status='pending' ORDER BY created_at;"
            : BuildIdQuery(transitionIds);
        Add(command, "@w", workspaceId);
        for (var i = 0; i < transitionIds.Count; i++)
            Add(command, $"@id{i}", transitionIds[i]);

        var transitions = new List<TransitionRecord>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            transitions.Add(new TransitionRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                JsonSerializer.Deserialize<List<string>>(reader.GetString(4)) ?? [],
                reader.GetString(5), DateTimeOffset.Parse(reader.GetString(6))));
        return transitions;
    }

    public async Task SetTransitionStatusAsync(string workspaceId, string transitionId,
        string status, CancellationToken ct = default)
    {
        await using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE transitions SET status=@s WHERE workspace_id=@w AND id=@id;";
        Add(command, "@s", status);
        Add(command, "@w", workspaceId);
        Add(command, "@id", transitionId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task AppendEventAsync(string workspaceId, string kind, string payloadJson,
        CancellationToken ct = default)
    {
        await using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO state_events (workspace_id, kind, payload_json, occurred_at) VALUES (@w, @k, @p, @t);";
        Add(command, "@w", workspaceId);
        Add(command, "@k", kind);
        Add(command, "@p", payloadJson);
        Add(command, "@t", DateTimeOffset.UtcNow.ToString("o"));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<StateEvent>> GetEventsAsync(string workspaceId, int limit,
        CancellationToken ct = default)
    {
        await using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, kind, payload_json, occurred_at FROM state_events WHERE workspace_id=@w ORDER BY id DESC LIMIT @limit;";
        Add(command, "@w", workspaceId);
        Add(command, "@limit", limit);
        var events = new List<StateEvent>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            events.Add(new StateEvent(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3))));
        return events;
    }

    private static string BuildIdQuery(IReadOnlyList<string> ids)
    {
        var parameters = string.Join(", ", ids.Select((_, i) => $"@id{i}"));
        return $"SELECT id, from_state, to_state, summary, evidence_json, status, created_at FROM transitions WHERE workspace_id=@w AND id IN ({parameters});";
    }

    private static void Add(SqliteCommand command, string name, object value)
        => command.Parameters.AddWithValue(name, value);
}
