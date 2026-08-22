using System.Text.Json;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>SQLite-backed persistence for spawned agents, their transcripts, and domain events.
///     Lives in the same app database as SqliteStateStore and follows its connection and
///     serialization discipline (System.Text.Json defaults, "o"-format timestamps).</summary>
public sealed class SqliteAgentStore : IAgentStore
{
    private const string SpawnedEventType = "spawned";
    private const string CompletedEventType = "completed";

    /// <summary>Message fields without a dedicated column, serialized into agent_messages.meta_json.</summary>
    internal sealed record MessageMeta(DateTimeOffset Timestamp, IReadOnlyList<ToolCall>? ToolCalls, string? ToolCallId);

    private readonly AppDatabase _database;

    /// <summary>Single-writer gate: serializes all mutating operations so concurrent
    ///     callers never race inside SQLite transactions (e.g. transcript seq allocation).
    ///     Reads stay direct — SQLite handles concurrent readers natively.</summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public SqliteAgentStore(AppDatabase database)
        => _database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO agents (id, parent_id, depth, status, failure_reason, model_used, label, task_prompt, created_at, completed_at, final_report)
                VALUES (@id, @parent, @depth, @status, @failure, @model, @label, @prompt, @created, @completed, @report);
                """;
            BindRecord(command, record);
            await command.ExecuteNonQueryAsync(ct);
            return Result<string>.Success(record.Id.ToString());
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE agents SET parent_id=@parent, depth=@depth, status=@status, failure_reason=@failure,
                    model_used=@model, label=@label, task_prompt=@prompt, created_at=@created,
                    completed_at=@completed, final_report=@report
                WHERE id=@id;
                """;
            BindRecord(command, record);
            return await command.ExecuteNonQueryAsync(ct) == 0
                ? Result<string>.Failure(NotFound(record.Id))
                : Result<string>.Success(record.Id.ToString());
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
    {
        await using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, parent_id, depth, status, failure_reason, model_used, label, task_prompt,
                   created_at, completed_at, final_report
            FROM agents WHERE id=@id;
            """;
        Add(command, "@id", id.ToString());
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? Result<AgentRecord>.Success(ReadRecord(reader))
            : Result<AgentRecord>.Failure(NotFound(id));
    }

    public async Task<Result<string>> AppendMessageAsync(AgentId id, Message message, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = _database.Open();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

            if (!await AgentExistsAsync(connection, transaction, id, ct))
            {
                await transaction.RollbackAsync(ct);
                return Result<string>.Failure(NotFound(id));
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO agent_messages (agent_id, seq, role, content, meta_json)
                VALUES (@id, (SELECT COALESCE(MAX(seq), -1) + 1 FROM agent_messages WHERE agent_id=@id), @role, @content, @meta);
                """;
            Add(command, "@id", id.ToString());
            Add(command, "@role", message.Role.ToString());
            Add(command, "@content", message.Content);
            Add(command, "@meta", JsonSerializer.Serialize(
                new MessageMeta(message.Timestamp, message.ToolCalls, message.ToolCallId)));
            await command.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            return Result<string>.Success(id.ToString());
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(AgentId id, CancellationToken ct = default)
    {
        await using var connection = _database.Open();
        if (!await AgentExistsAsync(connection, transaction: null, id, ct))
            return Result<IReadOnlyList<Message>>.Failure(NotFound(id));

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT role, content, meta_json FROM agent_messages WHERE agent_id=@id ORDER BY seq;";
        Add(command, "@id", id.ToString());
        var messages = new List<Message>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var meta = JsonSerializer.Deserialize<MessageMeta>(reader.GetString(2))!;
            messages.Add(new Message(
                Enum.Parse<Role>(reader.GetString(0)),
                reader.GetString(1),
                meta.Timestamp,
                meta.ToolCalls,
                meta.ToolCallId));
        }
        return Result<IReadOnlyList<Message>>.Success(messages);
    }

    public async Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default)
    {
        await using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, parent_id, depth, status, failure_reason, model_used, label, task_prompt,
                   created_at, completed_at, final_report
            FROM agents WHERE parent_id=@parent ORDER BY created_at;
            """;
        Add(command, "@parent", parentId.ToString());
        var children = new List<AgentRecord>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            children.Add(ReadRecord(reader));
        return Result<IReadOnlyList<AgentRecord>>.Success(children);
    }

    public async Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
    {
        await using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, parent_id, depth, status, failure_reason, model_used, label, task_prompt,
                   created_at, completed_at, final_report
            FROM agents ORDER BY created_at;
            """;
        var records = new List<AgentRecord>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            records.Add(ReadRecord(reader));
        return Result<IReadOnlyList<AgentRecord>>.Success(records);
    }

    /// <summary>Persists an agent domain event as an agent_events row (state_events-style append).</summary>
    public async Task<Result<string>> AppendEventAsync(AgentDomainEvent domainEvent, CancellationToken ct = default)
    {
        await using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO agent_events (agent_id, occurred_at, type, payload_json) VALUES (@id, @at, @type, @payload);";
        Add(command, "@id", domainEvent.AgentId.ToString());
        Add(command, "@at", domainEvent.OccurredAt.ToString("o"));
        Add(command, "@type", EventTypeOf(domainEvent));
        Add(command, "@payload", JsonSerializer.Serialize(domainEvent, domainEvent.GetType()));
        await command.ExecuteNonQueryAsync(ct);
        return Result<string>.Success(domainEvent.AgentId.ToString());
    }

    /// <summary>Reloads an agent's persisted events in insertion order.</summary>
    public async Task<Result<IReadOnlyList<AgentDomainEvent>>> GetEventsAsync(AgentId agentId, CancellationToken ct = default)
    {
        await using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, payload_json FROM agent_events WHERE agent_id=@id ORDER BY id;";
        Add(command, "@id", agentId.ToString());
        var events = new List<AgentDomainEvent>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var type = reader.GetString(0);
            AgentDomainEvent? domainEvent = type switch
            {
                SpawnedEventType => JsonSerializer.Deserialize<AgentSpawned>(reader.GetString(1)),
                CompletedEventType => JsonSerializer.Deserialize<AgentCompleted>(reader.GetString(1)),
                _ => null,
            };
            if (domainEvent is null)
                return Result<IReadOnlyList<AgentDomainEvent>>.Failure(
                    new Error("UnknownEventType", $"agent event type '{type}' is not recognized."));
            events.Add(domainEvent);
        }
        return Result<IReadOnlyList<AgentDomainEvent>>.Success(events);
    }

    private static string EventTypeOf(AgentDomainEvent domainEvent)
        => domainEvent switch
        {
            AgentSpawned => SpawnedEventType,
            AgentCompleted => CompletedEventType,
            _ => throw new InvalidOperationException(
                $"unknown agent domain event type {domainEvent.GetType().Name}"),
        };

    private static async Task<bool> AgentExistsAsync(SqliteConnection connection, SqliteTransaction? transaction,
        AgentId id, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        if (transaction is not null) command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM agents WHERE id=@id;";
        Add(command, "@id", id.ToString());
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) > 0;
    }

    private static void BindRecord(SqliteCommand command, AgentRecord record)
    {
        Add(command, "@id", record.Id.ToString());
        Add(command, "@parent", (object?)record.ParentId?.ToString() ?? DBNull.Value);
        Add(command, "@depth", record.Depth);
        Add(command, "@status", (long)record.Status);
        Add(command, "@failure",
            record.FailureReason is null ? DBNull.Value : (object)(long)record.FailureReason.Value);
        Add(command, "@model", record.ModelUsed);
        Add(command, "@label", (object?)record.Label ?? DBNull.Value);
        Add(command, "@prompt", record.TaskPrompt);
        Add(command, "@created", record.CreatedAt.ToString("o"));
        Add(command, "@completed", (object?)record.CompletedAt?.ToString("o") ?? DBNull.Value);
        Add(command, "@report", (object?)record.FinalReport ?? DBNull.Value);
    }

    private static AgentRecord ReadRecord(SqliteDataReader reader) => new(
        new AgentId(Guid.Parse(reader.GetString(0))),
        reader.IsDBNull(1) ? null : new AgentId(Guid.Parse(reader.GetString(1))),
        reader.GetInt32(2),
        (AgentStatus)reader.GetInt32(3),
        reader.IsDBNull(4) ? null : (AgentFailureReason?)reader.GetInt32(4),
        reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.GetString(7),
        DateTimeOffset.Parse(reader.GetString(8)),
        reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)),
        reader.IsDBNull(10) ? null : reader.GetString(10));

    private static Error NotFound(AgentId id)
        => new("NotFound", $"agent {id} was not found.");

    private static void Add(SqliteCommand command, string name, object value)
        => command.Parameters.AddWithValue(name, value);
}
