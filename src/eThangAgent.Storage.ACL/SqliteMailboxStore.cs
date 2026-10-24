using System.Globalization;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>SQLite-backed between-turn mailbox durability: one batch per agent, replaced
///     wholesale. Follows SqliteAgentStore's connection and timestamp discipline.</summary>
// Named decision (CA1001): process-lifetime singleton owned by the composition root.
#pragma warning disable CA1001 // Types that own disposable fields should be disposable
public sealed class SqliteMailboxStore(AppDatabase database) : IMailboxStore
{
  private readonly AppDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
  private readonly SemaphoreSlim _writeGate = new(1, 1);

  public async Task<Result<string>> PersistUndeliveredAsync(AgentId id, IReadOnlyList<PendingMessage> messages, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(messages);
    await _writeGate.WaitAsync(ct).ConfigureAwait(false);
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
#pragma warning restore CA2007
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait (mirrors SqliteAgentStore).
#pragma warning disable CA2007
      await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
#pragma warning restore CA2007
      using SqliteCommand clear = connection.CreateCommand();
      clear.Transaction = transaction;
      clear.CommandText = "DELETE FROM mailbox_messages WHERE agent_id=@id;";
      Add(clear, "@id", id.ToString());
      _ = await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

      for (int seq = 0; seq < messages.Count; seq++)
      {
        PendingMessage message = messages[seq];
        using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
                    INSERT INTO mailbox_messages (agent_id, seq, sender, urgency, text, created_at, message_id)
                    VALUES (@id, @seq, @sender, @urgency, @text, @created, @messageId);
                    """;
        Add(insert, "@id", id.ToString());
        Add(insert, "@seq", seq);
        Add(insert, "@sender", message.Sender);
        Add(insert, "@urgency", (long)message.Urgency);
        Add(insert, "@text", message.Text);
        Add(insert, "@created", message.At.ToString("o"));
        // The identity persists with the message (issue #92): a remainder that outlives
        // its run must stay addressable, or the sender could never learn its fate.
        Add(insert, "@messageId", message.Id.ToString("D"));
        _ = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      }

      await transaction.CommitAsync(ct).ConfigureAwait(false);
      return Result.Success(id.ToString());
    }
    finally
    {
      _ = _writeGate.Release();
    }
  }

  public async Task<Result<IReadOnlyList<PendingMessage>>> LoadUndeliveredAsync(AgentId id, CancellationToken ct = default)
  {
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT sender, urgency, text, created_at, message_id FROM mailbox_messages WHERE agent_id=@id ORDER BY seq;";
    Add(command, "@id", id.ToString());
    List<PendingMessage> messages = [];
    using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
    while (await reader.ReadAsync(ct).ConfigureAwait(false))
    {
      // A row written before V19 carries no identity; minting one on load keeps the
      // message deliverable and addressable from here on (issue #92). The original
      // handle is genuinely gone - it was never persisted - so there is nothing to
      // recover, and inventing a receipt for the old one would be a lie.
      bool noId = await reader.IsDBNullAsync(4, ct).ConfigureAwait(false);
      Guid messageId = noId || !Guid.TryParse(reader.GetString(4), out Guid parsed)
          ? Guid.NewGuid()
          : parsed;
      messages.Add(new PendingMessage(
          reader.GetString(2),
          (MessageUrgency)reader.GetInt64(1),
          DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
          reader.GetString(0),
          messageId));
    }

    return Result.Success<IReadOnlyList<PendingMessage>>(messages);
  }

  public async Task<Result<string>> ClearAsync(AgentId id, CancellationToken ct = default)
  {
    await _writeGate.WaitAsync(ct).ConfigureAwait(false);
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
#pragma warning restore CA2007
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = "DELETE FROM mailbox_messages WHERE agent_id=@id;";
      Add(command, "@id", id.ToString());
      _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      return Result.Success(id.ToString());
    }
    finally
    {
      _ = _writeGate.Release();
    }
  }

  private static void Add(SqliteCommand command, string name, object value)
      => command.Parameters.AddWithValue(name, value);
}
#pragma warning restore CA1001 // Types that own disposable fields should be disposable
