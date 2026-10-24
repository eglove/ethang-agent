using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Storage.ACL.Tests;

// The reasoning trace's persistence contract (issue #263): an agent_events row that
// round-trips through the store, readable from the app database alone. No schema change
// - the append-only event table already existed.
public sealed class SqliteAgentStoreReasoningTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(
      Path.GetTempPath(), $"ethang-reasoning-{Guid.NewGuid():N}.db");
  private readonly SqliteAgentStore _store;

  public SqliteAgentStoreReasoningTests()
      => _store = new SqliteAgentStore(new AppDatabase(_dbPath));

  public void Dispose()
  {
    GC.SuppressFinalize(this);
    // Named decision (CA1031): temp-db cleanup is best effort.
#pragma warning disable CA1031, S108 // Do not catch general exception types
    try
    {
      File.Delete(_dbPath);
    }
    catch { }
#pragma warning restore CA1031, S108
  }

  [Fact]
  public async Task AppendEventAsync_ReasoningRecord_RoundTripsEveryField()
  {
    AgentId id = AgentId.NewId();
    AgentReasoningRecorded recorded = new(id,
        new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero),
        Guid.Parse("11111111-2222-3333-4444-555555555555"), 3, "the model thought this");

    Assert.True((await _store.AppendEventAsync(recorded, ct: TestContext.Current.CancellationToken)).IsSuccess);

    Result<IReadOnlyList<AgentDomainEvent>> events =
        await _store.GetEventsAsync(id, ct: TestContext.Current.CancellationToken);

    Assert.True(events.IsSuccess);
    AgentReasoningRecorded reloaded = Assert.IsType<AgentReasoningRecorded>(Assert.Single(events.Value));
    Assert.Equal(recorded, reloaded);
  }

  [Fact]
  public async Task AppendEventAsync_ReasoningRows_AreOrderedWithTheOtherEventKinds()
  {
    AgentId id = AgentId.NewId();
    DateTimeOffset at = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);
    AgentSpawned spawned = new(id, at, 1, "provider/model", "child");
    AgentReasoningRecorded first = new(id, at.AddSeconds(1), Guid.NewGuid(), 0, "one");
    AgentReasoningRecorded second = new(id, at.AddSeconds(2), Guid.NewGuid(), 0, "two");
    AgentCompleted completed = new(id, at.AddSeconds(3), AgentStatus.Completed, null);

    _ = await _store.AppendEventAsync(spawned, ct: TestContext.Current.CancellationToken);
    _ = await _store.AppendEventAsync(first, ct: TestContext.Current.CancellationToken);
    _ = await _store.AppendEventAsync(second, ct: TestContext.Current.CancellationToken);
    _ = await _store.AppendEventAsync(completed, ct: TestContext.Current.CancellationToken);

    Result<IReadOnlyList<AgentDomainEvent>> events =
        await _store.GetEventsAsync(id, ct: TestContext.Current.CancellationToken);

    Assert.True(events.IsSuccess);
    Assert.Equal(4, events.Value.Count);
    Assert.Equal(first, events.Value[1]);
    Assert.Equal(second, events.Value[2]);
  }

  [Fact]
  public async Task AppendEventAsync_ReasoningTrace_IsReadableFromTheDatabaseDirectly()
  {
    // The acceptance criterion "after a session, the reasoning produced in each turn is
    // readable from the app database" - read it the way a human or an eval would, with
    // a plain SELECT over the event table, not through the store API.
    AgentId id = AgentId.NewId();
    _ = await _store.AppendEventAsync(
        new AgentReasoningRecorded(id, DateTimeOffset.UtcNow, Guid.NewGuid(), 0, "visible in SQL"),
        ct: TestContext.Current.CancellationToken);

    using Microsoft.Data.Sqlite.SqliteConnection connection = new AppDatabase(_dbPath).OpenReadOnly();
    using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT type, payload_json FROM agent_events WHERE agent_id=@id;";
    _ = command.Parameters.AddWithValue("@id", id.ToString());
    using Microsoft.Data.Sqlite.SqliteDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
    Assert.Equal("reasoning", reader.GetString(0));
    Assert.Contains("visible in SQL", reader.GetString(1), StringComparison.Ordinal);
  }
}
