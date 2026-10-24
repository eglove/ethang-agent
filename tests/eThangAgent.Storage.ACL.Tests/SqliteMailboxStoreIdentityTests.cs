using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Issue #92: a persisted remainder must stay ADDRESSABLE across the settle that
///     persisted it, or a sender could never learn what became of a message that outlived
///     its run. The identity therefore round-trips through the store, and a row written
///     before V19 (no id) still loads as a deliverable message.</summary>
public sealed class SqliteMailboxStoreIdentityTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ethang-mailbox-{Guid.NewGuid():N}.db");

  private string NewDbPath() => _dbPath;

  public void Dispose()
  {
    GC.SuppressFinalize(this);
    // Named decision (CA1031): temp-db cleanup is best effort.
#pragma warning disable CA1031, S108 // Do not catch general exception types
    try
    {
      File.Delete(_dbPath);
    }
    catch (IOException)
    {
    }
#pragma warning restore CA1031, S108 // Do not catch general exception types
  }

  [Fact]
  public async Task PersistedMessage_RoundTripsItsIdentity()
  {
    AppDatabase db = new(NewDbPath());
    SqliteMailboxStore store = new(db);
    AgentId child = AgentId.NewId();
    PendingMessage message = PendingMessage.New("undelivered steer", MessageUrgency.Attention, "parent");

    _ = await store.PersistUndeliveredAsync(child, [message], TestContext.Current.CancellationToken).ConfigureAwait(true);
    Result<IReadOnlyList<PendingMessage>> loaded =
        await store.LoadUndeliveredAsync(child, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(loaded.IsSuccess);
    PendingMessage restored = Assert.Single(loaded.Value);
    Assert.Equal(message.Id, restored.Id);
    Assert.Equal(message.Text, restored.Text);
    Assert.Equal(message.Sender, restored.Sender);
    Assert.Equal(message.Urgency, restored.Urgency);
  }

  [Fact]
  public async Task MultipleMessages_KeepTheirOwnIdentitiesInOrder()
  {
    AppDatabase db = new(NewDbPath());
    SqliteMailboxStore store = new(db);
    AgentId child = AgentId.NewId();
    PendingMessage first = PendingMessage.New("first", MessageUrgency.Normal, "parent");
    PendingMessage second = PendingMessage.New("second", MessageUrgency.Urgent, "parent");

    _ = await store.PersistUndeliveredAsync(child, [first, second], TestContext.Current.CancellationToken).ConfigureAwait(true);
    Result<IReadOnlyList<PendingMessage>> loaded =
        await store.LoadUndeliveredAsync(child, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(loaded.IsSuccess);
    Assert.Equal([first.Id, second.Id], [.. loaded.Value.Select(m => m.Id)]);
  }

  /// <summary>A pre-V19 row has no identity; loading mints one so the message stays
  ///     deliverable and addressable from here on. The ORIGINAL handle was never persisted,
  ///     so there is nothing to recover — and inventing a receipt for it would be a lie.</summary>
  [Fact]
  public async Task LegacyRowWithoutIdentity_LoadsWithAMintedIdentity()
  {
    AppDatabase db = new(NewDbPath());
    SqliteMailboxStore store = new(db);
    AgentId child = AgentId.NewId();
    // Write a row the way the pre-V19 schema did: message_id left NULL.
    WriteLegacyRow(db, child);

    Result<IReadOnlyList<PendingMessage>> loaded =
        await store.LoadUndeliveredAsync(child, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(loaded.IsSuccess);
    PendingMessage restored = Assert.Single(loaded.Value);
    Assert.Equal("legacy steer", restored.Text);
    Assert.NotEqual(Guid.Empty, restored.Id);
  }

  private static void WriteLegacyRow(AppDatabase db, AgentId child)
  {
    using Microsoft.Data.Sqlite.SqliteConnection connection = db.Open();
    using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
    command.CommandText =
        "INSERT INTO mailbox_messages (agent_id, seq, sender, urgency, text, created_at, message_id) " +
        "VALUES (@id, 0, 'parent', 0, 'legacy steer', @created, NULL);";
    _ = command.Parameters.AddWithValue("@id", child.ToString());
    _ = command.Parameters.AddWithValue("@created", DateTimeOffset.UtcNow.ToString("o"));
    _ = command.ExecuteNonQuery();
  }
}
