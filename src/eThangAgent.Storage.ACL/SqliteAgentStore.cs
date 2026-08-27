using System.Globalization;
using System.Text.Json;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;


/// <summary>SQLite-backed persistence for spawned agents, their transcripts, and domain events.
///     Lives in the same app database as SqliteStateStore and follows its connection and
///     serialization discipline (System.Text.Json defaults, "o"-format timestamps).</summary>
// Named decision (CA1001): process-lifetime singleton owned by the composition root;
// disposing the write gate on teardown adds no value.
#pragma warning disable CA1001 // Types that own disposable fields should be disposable
public sealed class SqliteAgentStore(AppDatabase database) : IAgentStore
{
  private const string SpawnedEventType = "spawned";
  private const string CompletedEventType = "completed";

  /// <summary>Message fields without a dedicated column, serialized into agent_messages.meta_json.</summary>
  internal sealed record MessageMeta(DateTimeOffset Timestamp, IReadOnlyList<ToolCall>? ToolCalls, string? ToolCallId);

  private readonly AppDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

  /// <summary>Single-writer gate: serializes all mutating operations so concurrent
  ///     callers never race inside SQLite transactions (e.g. transcript seq allocation).
  ///     Reads stay direct — SQLite handles concurrent readers natively.</summary>
  private readonly SemaphoreSlim _writeGate = new(1, 1);

  public async Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(record);
    await _writeGate.WaitAsync(ct).ConfigureAwait(false);
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = """
                INSERT INTO agents (id, parent_id, depth, status, failure_reason, model_used, label, task_prompt, created_at, completed_at, final_report)
                VALUES (@id, @parent, @depth, @status, @failure, @model, @label, @prompt, @created, @completed, @report);
                """;
      BindRecord(command, record);
      _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      return Result.Success(record.Id.ToString());
    }
    finally
    {
      _ = _writeGate.Release();
    }
  }

  public async Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(record);
    await _writeGate.WaitAsync(ct).ConfigureAwait(false);
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = """
                UPDATE agents SET parent_id=@parent, depth=@depth, status=@status, failure_reason=@failure,
                    model_used=@model, label=@label, task_prompt=@prompt, created_at=@created,
                    completed_at=@completed, final_report=@report
                WHERE id=@id;
                """;
      BindRecord(command, record);
      return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0
          ? Result.Failure<string>(NotFound(record.Id))
          : Result.Success(record.Id.ToString());
    }
    finally
    {
      _ = _writeGate.Release();
    }
  }

  public async Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
  {
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = """
            SELECT id, parent_id, depth, status, failure_reason, model_used, label, task_prompt,
                   created_at, completed_at, final_report
            FROM agents WHERE id=@id;
            """;
    Add(command, "@id", id.ToString());
    using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
    return await reader.ReadAsync(ct).ConfigureAwait(false)
        ? Result.Success(ReadRecord(reader))
        : Result.Failure<AgentRecord>(NotFound(id));
  }

  public async Task<Result<string>> AppendMessageAsync(AgentId id, Message message, CancellationToken ct = default)
  {
    await _writeGate.WaitAsync(ct).ConfigureAwait(false);
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
#pragma warning restore CA2007

      if (!await AgentExistsAsync(connection, transaction, id, ct).ConfigureAwait(false))
      {
        await transaction.RollbackAsync(ct).ConfigureAwait(false);
        return Result.Failure<string>(NotFound(id));
      }

      using SqliteCommand command = connection.CreateCommand();
      command.Transaction = transaction;
      command.CommandText = """
                INSERT INTO agent_messages (agent_id, seq, role, content, meta_json)
                VALUES (@id, (SELECT COALESCE(MAX(seq), -1) + 1 FROM agent_messages WHERE agent_id=@id), @role, @content, @meta);
                """;
      ArgumentNullException.ThrowIfNull(message);
      Add(command, "@id", id.ToString());
      Add(command, "@role", message.Role.ToString());
      Add(command, "@content", message.Content);
      Add(command, "@meta", JsonSerializer.Serialize(
          new MessageMeta(message.Timestamp, message.ToolCalls, message.ToolCallId)));
      _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      await transaction.CommitAsync(ct).ConfigureAwait(false);
      return Result.Success(id.ToString());
    }
    finally
    {
      _ = _writeGate.Release();
    }
  }

  public async Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(AgentId id, CancellationToken ct = default)
  {
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
    if (!await AgentExistsAsync(connection, transaction: null, id, ct).ConfigureAwait(false))
    {
      return Result.Failure<IReadOnlyList<Message>>(NotFound(id));
    }

    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT role, content, meta_json FROM agent_messages WHERE agent_id=@id ORDER BY seq;";
    Add(command, "@id", id.ToString());
    List<Message> messages = [];
    using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
    while (await reader.ReadAsync(ct).ConfigureAwait(false))
    {
      MessageMeta meta = JsonSerializer.Deserialize<MessageMeta>(reader.GetString(2))!;
      messages.Add(new Message(
          Enum.Parse<Role>(reader.GetString(0)),
          reader.GetString(1),
          meta.Timestamp,
          meta.ToolCalls,
          meta.ToolCallId));
    }
    return Result.Success<IReadOnlyList<Message>>(messages);
  }

  public async Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default)
  {
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = """
            SELECT id, parent_id, depth, status, failure_reason, model_used, label, task_prompt,
                   created_at, completed_at, final_report
            FROM agents WHERE parent_id=@parent ORDER BY created_at;
            """;
    Add(command, "@parent", parentId.ToString());
    List<AgentRecord> children = [];
    using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
    while (await reader.ReadAsync(ct).ConfigureAwait(false))
    {
      children.Add(ReadRecord(reader));
    }

    return Result.Success<IReadOnlyList<AgentRecord>>(children);
  }

  public async Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
  {
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = """
            SELECT id, parent_id, depth, status, failure_reason, model_used, label, task_prompt,
                   created_at, completed_at, final_report
            FROM agents ORDER BY created_at;
            """;
    List<AgentRecord> records = [];
    using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
    while (await reader.ReadAsync(ct).ConfigureAwait(false))
    {
      records.Add(ReadRecord(reader));
    }

    return Result.Success<IReadOnlyList<AgentRecord>>(records);
  }

  /// <summary>Persists an agent domain event as an agent_events row (state_events-style append).</summary>
  public async Task<Result<string>> AppendEventAsync(AgentDomainEvent domainEvent, CancellationToken ct = default)
  {
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "INSERT INTO agent_events (agent_id, occurred_at, type, payload_json) VALUES (@id, @at, @type, @payload);";
    ArgumentNullException.ThrowIfNull(domainEvent);
    Add(command, "@id", domainEvent.AgentId.ToString());
    Add(command, "@at", domainEvent.OccurredAt.ToString("o"));
    Add(command, "@type", EventTypeOf(domainEvent));
    Add(command, "@payload", JsonSerializer.Serialize(domainEvent, domainEvent.GetType()));
    _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    return Result.Success(domainEvent.AgentId.ToString());
  }

  /// <summary>Reloads an agent's persisted events in insertion order.</summary>
  public async Task<Result<IReadOnlyList<AgentDomainEvent>>> GetEventsAsync(AgentId agentId, CancellationToken ct = default)
  {
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT type, payload_json FROM agent_events WHERE agent_id=@id ORDER BY id;";
    Add(command, "@id", agentId.ToString());
    List<AgentDomainEvent> events = [];
    using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
    while (await reader.ReadAsync(ct).ConfigureAwait(false))
    {
      string type = reader.GetString(0);
      AgentDomainEvent? domainEvent = type switch
      {
        SpawnedEventType => JsonSerializer.Deserialize<AgentSpawned>(reader.GetString(1)),
        CompletedEventType => JsonSerializer.Deserialize<AgentCompleted>(reader.GetString(1)),
        _ => null,
      };
      if (domainEvent is null)
      {
        return Result.Failure<IReadOnlyList<AgentDomainEvent>>(
            new DomainError("UnknownEventType", $"agent event type '{type}' is not recognized."));
      }

      events.Add(domainEvent);
    }
    return Result.Success<IReadOnlyList<AgentDomainEvent>>(events);
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
    using SqliteCommand command = connection.CreateCommand();
    if (transaction is not null)
    {
      command.Transaction = transaction;
    }

    command.CommandText = "SELECT COUNT(*) FROM agents WHERE id=@id;";
    Add(command, "@id", id.ToString());
    return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0;
  }

  private static void BindRecord(SqliteCommand command, AgentRecord record)
  {
    Add(command, "@id", record.Id.ToString());
    Add(command, "@parent", (object?)record.ParentId?.ToString() ?? DBNull.Value);
    Add(command, "@depth", record.Depth);
    Add(command, "@status", (long)record.Status);
    Add(command, "@failure",
        record.FailureReason is null ? DBNull.Value : (long)record.FailureReason.Value);
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
      DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
      reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture),
      reader.IsDBNull(10) ? null : reader.GetString(10));

  private static DomainError NotFound(AgentId id)
      => new("NotFound", $"agent {id} was not found.");

  private static void Add(SqliteCommand command, string name, object value)
      => command.Parameters.AddWithValue(name, value);
}
#pragma warning restore CA1001 // Types that own disposable fields should be disposable
#pragma warning restore CA1001 // Types that own disposable fields should be disposable
