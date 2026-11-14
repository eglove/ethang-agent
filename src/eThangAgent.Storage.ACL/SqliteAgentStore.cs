using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;


/// <summary>SQLite-backed persistence for spawned agents, their transcripts, and domain events.
///     Lives in the same app database as SqliteStateStore and follows its connection and
///     serialization discipline (System.Text.Json defaults, "o"-format timestamps).</summary>
// Named decision (CA1001): process-lifetime singleton owned by the composition root —
// disposing the write gate on teardown adds no value.
#pragma warning disable CA1001 // Types that own disposable fields should be disposable
public sealed class SqliteAgentStore(
    AppDatabase database,
    Func<string?, IImageStore?>? imagesFor = null) : IAgentStore, IAgentEventLog
{
  private const string SpawnedEventType = "spawned";
  private const string CompletedEventType = "completed";
  private const string ReasoningEventType = "reasoning";
  private const string UsageEventType = "usage";

  /// <summary>One persisted content part: the meta_json entry shape. Concrete by design -
  ///     System.Text.Json cannot deserialize the abstract MessagePart.
  ///     <para>Exactly one payload field is populated and the unpopulated one is absent from
  ///     the JSON: <see cref="Base64Data"/> for an inline image, <see cref="Path"/> for a
  ///     reference to a stored image. A row written before references existed carries
  ///     Base64Data alone and deserializes unchanged.</para>
  ///     <para>A THIRD SHAPE RECORDS A REMOVAL: <see cref="RemovedBytes"/> is what V43 left in place
  ///     of a payload it stripped from history, so a row that lost its picture still says how large
  ///     the picture was. It carries no payload and no path, and it rebuilds to NO part - the strip
  ///     is a one-way removal of bytes, not a new kind of image.</para></summary>
  internal sealed record MessageMetaPart(string MediaType,
      [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Base64Data = null,
      [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Path = null,
      [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RemovedBytes = null);

  /// <summary>Message fields without a dedicated column, serialized into agent_messages.meta_json.
  ///     Parts (content parts such as tool-produced images) ride here as a nullable entry list;
  ///     rows written before parts existed carry no parts key and read back with Parts null.
  ///     <para>A NULL ENTRY records an image that did not land: the store could not produce a file
  ///     for it and deliberately kept no payload in the row, so the entry's only job is to hold the
  ///     position its part occupied. The message's own text is what says an image was there.</para>
  ///     </summary>
  internal sealed record MessageMeta(DateTimeOffset Timestamp, IReadOnlyList<ToolCall>? ToolCalls,
      string? ToolCallId, IReadOnlyList<MessageMetaPart?>? Parts = null);

  /// <summary>Maps conversation parts onto the meta_json entry shape, persisting an image
  ///     payload as a FILE PLUS A POINTER.
  ///     <para>AN IMAGE WHOSE FILE THE SINK FAILED TO WRITE IS NOT STORED AS BYTES INSTEAD. A null
  ///     entry means exactly that: no reference names a file that was never written, and the
  ///     payload does not fall back into the row. The alternative - keeping a 20 MB screenshot
  ///     inline because its file could not be written - is the payload shape this store exists to
  ///     remove, and it would silently reappear on exactly the path that is already failing.</para>
  ///     <para>Part kinds with no entry representation are a programmer error (extend the mapping
  ///     with the kind), never a silent drop.</para>
  ///     <para>THE STORE IS WHERE THE SIZE LANDS, not the conversation: the message keeps its
  ///     inline part, so the request the producing turn builds still carries the bytes and the
  ///     model still sees what its tool just captured. What changes is the ROW.</para></summary>
  private async Task<IReadOnlyList<MessageMetaPart?>?> ToMetaPartsAsync(
      IReadOnlyList<MessagePart>? parts, string? sessionId, CancellationToken ct)
  {
    if (parts is not { Count: > 0 })
    {
      return null;
    }

    MessageMetaPart?[] entries = new MessageMetaPart?[parts.Count];
    for (int i = 0; i < parts.Count; i++)
    {
      entries[i] = await ToMetaPartAsync(parts[i], sessionId, ct).ConfigureAwait(false);
    }

    return entries;
  }

  private async Task<MessageMetaPart?> ToMetaPartAsync(MessagePart part, string? sessionId, CancellationToken ct) => part switch
  {
    MessagePart.ImagePart image when IsBelowThreshold(image.Base64Data) =>
        new(image.MediaType, Base64Data: image.Base64Data),
    MessagePart.ImagePart image => await PersistPayloadAsync(image, sessionId, ct).ConfigureAwait(false),
    MessagePart.ImageReferencePart reference => new(reference.MediaType, Path: reference.Path),
    _ => throw new InvalidOperationException(
        $"message part type {part.GetType().Name} has no meta_json representation."),
  };

  /// <summary>True when a base64 payload is small enough to stay in its row. Measured on the
  ///     DECODED length, which is the unit the threshold is declared in: a payload's text is a
  ///     third larger than its bytes, so measuring the text would move the boundary with the
  ///     encoding.</summary>
  private static bool IsBelowThreshold(string base64Data) =>
      ImagePartMarker.DecodedByteCount(base64Data) < ImageLimits.InlineThresholdBytes;

  /// <summary>Writes an over-threshold payload to the session's image store and returns the
  ///     entry that NAMES it.
  ///     <para>NO SINK RESOLVABLE IS NOT A FAILURE: a container composed without an image store
  ///     (a unit test, a legacy wiring, the catalog's read-only store) has nowhere to put a file,
  ///     so the payload keeps the inline shape it always had. Losing the picture would be a worse
  ///     answer than a large row.</para>
  ///     <para>A WIRED SINK THAT FAILS IS A DIFFERENT FACT: the file it was asked for is not
  ///     there, so NULL is returned - no reference names a file that was never written, and the
  ///     payload is NOT smuggled back into the row the reference existed to keep it out of. That
  ///     loss is recorded in the message's own text by <see
  ///     cref="ImagePartMarker.MarkerForUnstored"/>, which <see
  ///     cref="ContentNamingStoredImages"/> appends.</para></summary>
  private async Task<MessageMetaPart?> PersistPayloadAsync(MessagePart.ImagePart image,
      string? sessionId, CancellationToken ct)
  {
    if (_imagesFor?.Invoke(sessionId) is not { } store)
    {
      return new(image.MediaType, Base64Data: image.Base64Data);
    }

    Result<StoredImage> stored = await store
        .PersistBase64Async(image.MediaType, image.Base64Data, ct).ConfigureAwait(false);
    return stored.IsSuccess ? new MessageMetaPart(stored.Value.MediaType, Path: stored.Value.Path) : null;
  }



  /// <summary>Rebuilds conversation parts from their meta_json entries; empty stays null
  ///     so parts-less messages keep the legacy shape. A null entry is an image that did not
  ///     land - the store wrote no reference and kept no payload - so it contributes NO part, and
  ///     the message's own text is what says an image was there. An entry carrying neither
  ///     payload names no image and is a programmer error, never a silent drop.</summary>
  private static List<MessagePart>? FromMetaParts(IReadOnlyList<MessageMetaPart?>? entries)
  {
    if (entries is not { Count: > 0 })
    {
      return null;
    }

    List<MessagePart> parts = [.. entries.OfType<MessageMetaPart>().Select(FromMetaPart).OfType<MessagePart>()];
    return parts.Count > 0 ? parts : null;
  }


  /// <summary>The message's text with one line per image that became a FILE, naming the
  ///     absolute path, and one line per image that did NOT land, naming its media type and
  ///     size. The content column is what a transcript read hands back, so without these lines
  ///     the outcome of an image would live only in meta_json and nothing would tell the model
  ///     where the picture went - or that one was lost. The message's own text is untouched; an
  ///     image that stayed inline adds no line, because its bytes are the part.</summary>
  private static string ContentNamingStoredImages(string content,
      IReadOnlyList<MessagePart>? parts, IReadOnlyList<MessageMetaPart?>? entries)
  {
    if (parts is not { Count: > 0 } || entries is not { Count: > 0 })
    {
      return content;
    }

    StringBuilder named = new(content);
    for (int i = 0; i < parts.Count && i < entries.Count; i++)
    {
      if (parts[i] is MessagePart.ImagePart && entries[i] is { Path: { } path, MediaType: { } mediaType })
      {
        _ = named.Append('\n').Append(ImagePartMarker.MarkerForStored(mediaType, path));
      }
      else if (entries[i] is null && parts[i] is MessagePart.ImagePart unstored)
      {
        _ = named.Append('\n').Append(ImagePartMarker.MarkerForUnstored(unstored.MediaType,
            ImagePartMarker.DecodedByteCount(unstored.Base64Data)));
      }
    }

    return named.ToString();
  }

  /// <summary>The part an entry rebuilds to, or NULL for an entry that records a REMOVAL. V43 left
  ///     <see cref="MessageMetaPart.RemovedBytes"/> where a payload used to be, so the row still says
  ///     an image was there and how large it was; there is no longer a picture to hand a consumer,
  ///     and inventing one would be the lie the marker exists to prevent.
  ///     <para>IT REBUILDS TO NO PART rather than to a text part carrying the marker: the part
  ///     vocabulary has no meta representation for text, so a part invented here would make a
  ///     stripped transcript unpersistable - a compaction or a flush of it would throw. The record
  ///     lives in meta_json, which is where the strip put it and where a reader can find it.</para>
  ///     </summary>
  private static MessagePart? FromMetaPart(MessageMetaPart entry) => entry switch
  {
    { Path: { } path } => new MessagePart.ImageReferencePart(path, entry.MediaType),
    { Base64Data: { } base64Data } => new MessagePart.ImagePart(entry.MediaType, base64Data),
    { RemovedBytes: not null } => null,
    _ => throw new InvalidOperationException(
        $"a meta_json part claiming media type '{entry.MediaType}' carries none of Base64Data, Path or RemovedBytes."),
  };

  private readonly AppDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

  /// <summary>Resolves the session's image store by session id. Null (legacy wiring, a unit
  ///     test) leaves every image inline in its row, which is the shape this store wrote before
  ///     references existed - the legacy-wiring rule every seam in this codebase follows.</summary>
  private readonly Func<string?, IImageStore?>? _imagesFor = imagesFor;

  /// <summary>Single-writer gate: serializes all mutating operations so concurrent
  ///     callers never race inside SQLite transactions (e.g. transcript seq allocation).
  ///     Reads stay direct — SQLite handles concurrent readers natively.</summary>
  private readonly SemaphoreSlim _writeGate = new(1, 1);

  public async Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(record);
    if (RefuseUnnormalizedScope(record) is { } refusal)
    {
      return Result.Failure<string>(refusal);
    }

    await _writeGate.WaitAsync(ct).ConfigureAwait(false);
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = """
                INSERT INTO agents (id, status, failure_reason, model_used, label, task_prompt, created_at, completed_at, final_report, workspace_id, provider, attempts, phase, contract)
                VALUES (@id, @status, @failure, @model, @label, @prompt, @created, @completed, @report, @workspace, @provider, @attempts, @phase, @contract);
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
    if (RefuseUnnormalizedScope(record) is { } refusal)
    {
      return Result.Failure<string>(refusal);
    }

    await _writeGate.WaitAsync(ct).ConfigureAwait(false);
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = """
                UPDATE agents SET status=@status, failure_reason=@failure,
                    model_used=@model, label=@label, task_prompt=@prompt, created_at=@created,
                    completed_at=@completed, final_report=@report, workspace_id=@workspace, provider=@provider,
                    attempts=@attempts, phase=@phase, contract=@contract
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
            SELECT id, status, failure_reason, model_used, label, task_prompt,
                   created_at, completed_at, final_report, workspace_id, provider, attempts, phase, contract
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
      IReadOnlyList<MessageMetaPart?>? parts = await ToMetaPartsAsync(
          message.Parts, id.ToString(), ct).ConfigureAwait(false);
      Add(command, "@id", id.ToString());
      Add(command, "@role", message.Role.ToString());
      Add(command, "@content", ContentNamingStoredImages(message.Content, message.Parts, parts));
      Add(command, "@meta", JsonSerializer.Serialize(
          new MessageMeta(message.Timestamp, message.ToolCalls, message.ToolCallId, parts)));
      _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      await transaction.CommitAsync(ct).ConfigureAwait(false);
      return Result.Success(id.ToString());
    }
    finally
    {
      _ = _writeGate.Release();
    }
  }

  /// <inheritdoc cref="IAgentStore.ReplaceTranscriptAsync"/>
  public async Task<Result<string>> ReplaceTranscriptAsync(AgentId id, IReadOnlyList<Message> messages, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(messages);
    if (messages.Count == 0)
    {
      return Result.Failure<string>(new DomainError("EmptyConversation",
        "The transcript replacement is empty; the transcript never becomes empty."));
    }

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
      command.CommandText = "DELETE FROM agent_messages WHERE agent_id=@id;";
      Add(command, "@id", id.ToString());
      _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

      command.CommandText = "INSERT INTO agent_messages (agent_id, seq, role, content, meta_json) "
          + "VALUES (@id, @seq, @role, @content, @meta);";


      for (int seq = 0; seq < messages.Count; seq++)
      {
        Message message = messages[seq];
        command.Parameters.Clear();
        Add(command, "@id", id.ToString());
        Add(command, "@seq", seq);
        Add(command, "@role", message.Role.ToString());
        IReadOnlyList<MessageMetaPart?>? parts = await ToMetaPartsAsync(
            message.Parts, id.ToString(), ct).ConfigureAwait(false);
        Add(command, "@content", ContentNamingStoredImages(message.Content, message.Parts, parts));
        Add(command, "@meta", JsonSerializer.Serialize(
            new MessageMeta(message.Timestamp, message.ToolCalls, message.ToolCallId, parts)));
        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      }

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
          meta.ToolCallId,
          Parts: FromMetaParts(meta.Parts)));
    }
    return Result.Success<IReadOnlyList<Message>>(messages);
  }

  /// <summary>The transcript from <paramref name="fromSeq"/> onward.</summary>
  public async Task<Result<TranscriptTail>> GetTranscriptTailAsync(AgentId id, int fromSeq,
      CancellationToken ct = default)
  {
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
    if (!await AgentExistsAsync(connection, transaction: null, id, ct).ConfigureAwait(false))
    {
      return Result.Failure<TranscriptTail>(NotFound(id));
    }

    using SqliteCommand command = connection.CreateCommand();
    // The COUNT comes first in the same statement so the total and the tail describe one
    // snapshot: two statements could straddle a concurrent append and report a count that
    // does not belong to the tail beside it.
    command.CommandText = """
        SELECT role, content, meta_json,
               (SELECT COUNT(*) FROM agent_messages WHERE agent_id=@id)
        FROM agent_messages WHERE agent_id=@id AND seq >= @from ORDER BY seq;
        """;
    Add(command, "@id", id.ToString());
    Add(command, "@from", fromSeq);
    List<Message> messages = [];
    int total = 0;
    using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
    while (await reader.ReadAsync(ct).ConfigureAwait(false))
    {
      total = reader.GetInt32(3);
      MessageMeta meta = JsonSerializer.Deserialize<MessageMeta>(reader.GetString(2))!;
      messages.Add(new Message(
          Enum.Parse<Role>(reader.GetString(0)),
          reader.GetString(1),
          meta.Timestamp,
          meta.ToolCalls,
          meta.ToolCallId,
          Parts: FromMetaParts(meta.Parts)));
    }

    if (messages.Count == 0)
    {
      // No row carried the count, so the total is read on its own. This is the common case for
      // a poll that found nothing new, and it is one indexed count rather than a transcript.
      total = await CountMessagesAsync(connection, id, ct).ConfigureAwait(false);
    }

    return Result.Success(new TranscriptTail(messages, total));
  }

  /// <summary>How many messages the agent's transcript holds.</summary>
  private static async Task<int> CountMessagesAsync(SqliteConnection connection, AgentId id,
      CancellationToken ct)
  {
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM agent_messages WHERE agent_id=@id;";
    Add(command, "@id", id.ToString());
    return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false),
        CultureInfo.InvariantCulture);
  }

  /// <summary>Every stored-image path the agent's PERSISTED transcript names - the reference
  ///     set an orphan sweep is measured against.
  ///     <para>READ FROM THE ROWS, NOT FROM THE CONVERSATION: a path that reached a row is what
  ///     survives a restart, and a path that reached only an in-memory message names nothing a
  ///     later process could resolve. That is also why the answer comes from meta_json: the
  ///     content column names a converted image's path only as a rendering convenience.</para>
  ///     <para>A ROW WHOSE META_JSON WILL NOT PARSE IS AN ERROR HERE, never a silent omission: a
  ///     sweep driven by an incomplete reference set would delete the picture that row's own
  ///     message is about.</para></summary>
  public async Task<Result<IReadOnlySet<string>>> GetStoredImagePathsAsync(AgentId id,
      CancellationToken ct = default)
  {
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
    if (!await AgentExistsAsync(connection, transaction: null, id, ct).ConfigureAwait(false))
    {
      return Result.Failure<IReadOnlySet<string>>(NotFound(id));
    }

    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT meta_json FROM agent_messages WHERE agent_id=@id AND meta_json IS NOT NULL;";
    Add(command, "@id", id.ToString());
    List<string> paths = [];
    try
    {
      using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
      while (await reader.ReadAsync(ct).ConfigureAwait(false))
      {
        MessageMeta meta = JsonSerializer.Deserialize<MessageMeta>(reader.GetString(0))!;
        paths.AddRange((meta.Parts ?? []).OfType<MessageMetaPart>()
            .Where(part => part.Path is not null)
            .Select(part => part.Path!));
      }
    }
    catch (JsonException ex)
    {
      return Result.Failure<IReadOnlySet<string>>(new DomainError("TranscriptUnreadable",
          $"agent {id}'s transcript holds a row whose meta_json is not readable, so the images it names cannot be known: {ex.Message}"));
    }

    // The set is case-insensitive because two spellings of one Windows path are one file: a
    // sweep told otherwise would delete a picture its own transcript still names.
    return Result.Success<IReadOnlySet<string>>(paths.ToHashSet(StringComparer.OrdinalIgnoreCase));
  }


  public async Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
  {
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = """
            SELECT id, status, failure_reason, model_used, label, task_prompt,
                   created_at, completed_at, final_report, workspace_id, provider, attempts, phase, contract
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

  /// <summary>Persists an agent domain event as an agent_events row (state_events-style append).
  ///     This is the <see cref="IAgentEventLog"/> implementation too: the reasoning trace
  ///     (issue #263) is an ordinary row here, which is what makes it telemetry — the
  ///     conversation table and the request builder never see it.</summary>
  public async Task<Result<string>> AppendEventAsync(AgentDomainEvent domainEvent, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(domainEvent);
    // The same single-writer gate every other mutation takes: a reasoning trace lands
    // while the transcript is being written, and two writers racing inside SQLite is
    // exactly what the gate exists to prevent.
    await _writeGate.WaitAsync(ct).ConfigureAwait(false);
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = "INSERT INTO agent_events (agent_id, occurred_at, type, payload_json) VALUES (@id, @at, @type, @payload);";
      Add(command, "@id", domainEvent.AgentId.ToString());
      Add(command, "@at", domainEvent.OccurredAt.ToString("o"));
      Add(command, "@type", EventTypeOf(domainEvent));
      Add(command, "@payload", JsonSerializer.Serialize(domainEvent, domainEvent.GetType()));
      _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      return Result.Success(domainEvent.AgentId.ToString());
    }
    finally
    {
      _ = _writeGate.Release();
    }
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
        ReasoningEventType => JsonSerializer.Deserialize<AgentReasoningRecorded>(reader.GetString(1)),
        UsageEventType => JsonSerializer.Deserialize<AgentUsageRecorded>(reader.GetString(1)),
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
        AgentReasoningRecorded => ReasoningEventType,
        AgentUsageRecorded => UsageEventType,
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

  /// <summary>The named refusal for a row whose workspace id is a second spelling of one
  ///     directory, or null when the row is writable. A row with no workspace is writable: the
  ///     stamp is optional, and only a present one has to be an identity.</summary>
  /// <param name="record">The row about to be written.</param>
  private static DomainError? RefuseUnnormalizedScope(AgentRecord record)
  {
    if (record.WorkspaceId is not { } workspaceId)
    {
      return null;
    }

    string normalized = WorkspaceIdentity.Normalize(workspaceId);
    return string.Equals(workspaceId, normalized, StringComparison.Ordinal)
        ? null
        : new DomainError("WorkspaceNotNormalized",
            $"the agent row's workspace id '{workspaceId}' is not a normalized workspace identity; the normalized form is '{normalized}'.");
  }

  private static void BindRecord(SqliteCommand command, AgentRecord record)
  {
    BindIdentity(command, record);
    BindOutcome(command, record);
  }

  /// <summary>Binds the agent identity and lifecycle columns (id through created_at).</summary>
  private static void BindIdentity(SqliteCommand command, AgentRecord record)
  {
    Add(command, "@id", record.Id.ToString());
    Add(command, "@status", (long)record.Status);
    Add(command, "@failure",
        record.FailureReason is null ? DBNull.Value : (long)record.FailureReason.Value);
    Add(command, "@model", record.ModelUsed);
    Add(command, "@label", (object?)record.Label ?? DBNull.Value);
    Add(command, "@prompt", record.TaskPrompt);
    Add(command, "@created", record.CreatedAt.ToString("o"));
  }

  /// <summary>Binds the run-outcome and context columns (completed_at through contract).</summary>
  private static void BindOutcome(SqliteCommand command, AgentRecord record)
  {
    Add(command, "@completed", (object?)record.CompletedAt?.ToString("o") ?? DBNull.Value);
    Add(command, "@report", (object?)record.FinalReport ?? DBNull.Value);
    Add(command, "@workspace", (object?)record.WorkspaceId ?? DBNull.Value);
    Add(command, "@provider", (object?)record.Provider ?? DBNull.Value);
    Add(command, "@attempts", (long)record.Attempts);
    Add(command, "@phase", (object?)(record.Phase is { } phase ? (long)phase : null) ?? DBNull.Value);
    Add(command, "@contract", (object?)record.Contract ?? DBNull.Value);
  }

  private static AgentRecord ReadRecord(SqliteDataReader reader) => new(
      new AgentId(Guid.Parse(reader.GetString(0))),
      (AgentStatus)reader.GetInt32(1),
      reader.IsDBNull(2) ? null : (AgentFailureReason?)reader.GetInt32(2),
      reader.GetString(3),
      reader.IsDBNull(4) ? null : reader.GetString(4),
      reader.GetString(5),
      DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
      reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
      reader.IsDBNull(8) ? null : reader.GetString(8),
      reader.IsDBNull(9) ? null : reader.GetString(9),
      reader.IsDBNull(10) ? null : reader.GetString(10),
      reader.GetInt32(11),
      reader.IsDBNull(12) ? null : (AgentPhase?)reader.GetInt32(12),
      reader.IsDBNull(13) ? null : reader.GetString(13));

  private static DomainError NotFound(AgentId id)
      => new("NotFound", $"agent {id} was not found.");

  private static void Add(SqliteCommand command, string name, object value)
      => command.Parameters.AddWithValue(name, value);
}
#pragma warning restore CA1001 // Types that own disposable fields should be disposable
#pragma warning restore CA1001 // Types that own disposable fields should be disposable
