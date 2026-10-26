using System.Text.Json;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Storage.ACL.Tests;

// The usage row's persistence contract (issue #270): an agent_events row that round-trips
// through the store and is readable from the app database alone, with NO schema change -
// the append-only event table already existed (the shape #263 established).
public sealed class SqliteAgentStoreUsageTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(
      Path.GetTempPath(), $"ethang-usage-{Guid.NewGuid():N}.db");
  private readonly SqliteAgentStore _store;

  public SqliteAgentStoreUsageTests()
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
  public async Task AppendEventAsync_UsageRecord_RoundTripsEveryField()
  {
    AgentId id = AgentId.NewId();
    AgentUsageRecorded recorded = new(id,
        new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero),
        Guid.Parse("11111111-2222-3333-4444-555555555555"), 3, 100, 20, 64, 7);

    Assert.True((await _store.AppendEventAsync(recorded, ct: TestContext.Current.CancellationToken)).IsSuccess);

    Result<IReadOnlyList<AgentDomainEvent>> events =
        await _store.GetEventsAsync(id, ct: TestContext.Current.CancellationToken);

    Assert.True(events.IsSuccess);
    AgentUsageRecorded reloaded = Assert.IsType<AgentUsageRecorded>(Assert.Single(events.Value));
    Assert.Equal(recorded, reloaded);
  }

  [Fact]
  public async Task AppendEventAsync_UnreportedCounts_SurviveAsNull()
  {
    // NULL must survive the JSON round-trip: the measurement this row serves depends on
    // telling 'the provider reported nothing' from 'the provider reported zero'.
    AgentId id = AgentId.NewId();
    AgentUsageRecorded recorded = new(id, DateTimeOffset.UtcNow, Guid.NewGuid(), 0, 100, 20, null, null);

    _ = await _store.AppendEventAsync(recorded, ct: TestContext.Current.CancellationToken);
    Result<IReadOnlyList<AgentDomainEvent>> events =
        await _store.GetEventsAsync(id, ct: TestContext.Current.CancellationToken);

    Assert.True(events.IsSuccess);
    AgentUsageRecorded reloaded = Assert.IsType<AgentUsageRecorded>(Assert.Single(events.Value));
    Assert.Null(reloaded.CachedInputTokens);
    Assert.Null(reloaded.ReasoningTokens);
  }

  [Fact]
  public async Task AppendEventAsync_UsageRows_AreOrderedWithTheOtherEventKinds()
  {
    AgentId id = AgentId.NewId();
    DateTimeOffset at = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);
    AgentSpawned spawned = new(id, at, 1, "provider/model", "child");
    AgentUsageRecorded first = new(id, at.AddSeconds(1), Guid.NewGuid(), 0, 10, 2, 4, 1);
    AgentUsageRecorded second = new(id, at.AddSeconds(2), Guid.NewGuid(), 1, 11, 3, 5, 2);
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
  public async Task AppendEventAsync_ExtendedUsageRecord_RoundTripsPriceAndAttribution()
  {
    // Issue #22: price, call class, workspace, and provider ride the SAME row - no new
    // table, no migration - so a cost report is computable from agent_events alone.
    AgentId id = AgentId.NewId();
    AgentUsageRecorded recorded = new(id, DateTimeOffset.UtcNow, Guid.NewGuid(), 2, 100, 20, 64, 7,
        UsageCallClass.Compaction, "ws-root", "openrouter", 0.000003m, 0.0000003m, 0.000015m,
        ModelId: "cohere/north-mini-code:free", RequestedModelId: "openrouter/free",
        ServedModelId: "cohere/north-mini-code:free");

    Assert.True((await _store.AppendEventAsync(recorded, ct: TestContext.Current.CancellationToken)).IsSuccess);

    Result<IReadOnlyList<AgentDomainEvent>> events =
        await _store.GetEventsAsync(id, ct: TestContext.Current.CancellationToken);

    Assert.True(events.IsSuccess);
    AgentUsageRecorded reloaded = Assert.IsType<AgentUsageRecorded>(Assert.Single(events.Value));
    Assert.Equal(recorded, reloaded);
    Assert.Same(UsageCallClass.Compaction, reloaded.CallClass);
    Assert.Equal("ws-root", reloaded.WorkspaceId);
    Assert.Equal("openrouter", reloaded.Provider);
    Assert.Equal(0.000003m, reloaded.PromptPricePerToken);
    Assert.Equal(0.0000003m, reloaded.PromptCacheReadPricePerToken);
    Assert.Equal(0.000015m, reloaded.CompletionPricePerToken);
    // Issue #290: both model ids survive the round trip, and the pricing basis is the
    // SERVED id - the row can still say what actually ran after a restart.
    Assert.Equal("openrouter/free", reloaded.RequestedModelId);
    Assert.Equal("cohere/north-mini-code:free", reloaded.ServedModelId);
    Assert.Equal("cohere/north-mini-code:free", reloaded.ModelId);
    Assert.False(reloaded.PricedOnRequestedModel);
  }

  [Fact]
  public async Task GetEventsAsync_LegacyUsagePayload_ReadsWithNullMembersAndMainLoopClass()
  {
    // A row written before #22 carries none of the new members. It must still deserialize -
    // into the extended record with them null - and its class must read as main_loop,
    // because every row written before the class existed came from the loop.
    AgentId id = AgentId.NewId();
    string legacyPayload = JsonSerializer.Serialize(new
    {
      AgentId = new { Value = id.ToString() },
      OccurredAt = "2026-10-24T09:00:00+00:00",
      TurnId = Guid.NewGuid().ToString(),
      Iteration = 0,
      InputTokens = 100,
      OutputTokens = 20,
      CachedInputTokens = 64,
      ReasoningTokens = 7,
    });
    using (Microsoft.Data.Sqlite.SqliteConnection connection = new AppDatabase(_dbPath).Open())
    {
      using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
      command.CommandText =
          "INSERT INTO agent_events (agent_id, occurred_at, type, payload_json) VALUES (@id, @at, 'usage', @payload);";
      _ = command.Parameters.AddWithValue("@id", id.ToString());
      _ = command.Parameters.AddWithValue("@at", "2026-10-24T09:00:00.0000000+00:00");
      _ = command.Parameters.AddWithValue("@payload", legacyPayload);
      _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    Result<IReadOnlyList<AgentDomainEvent>> events =
        await _store.GetEventsAsync(id, ct: TestContext.Current.CancellationToken);

    Assert.True(events.IsSuccess);
    AgentUsageRecorded reloaded = Assert.IsType<AgentUsageRecorded>(Assert.Single(events.Value));
    Assert.Null(reloaded.CallClass);
    Assert.Null(reloaded.WorkspaceId);
    Assert.Null(reloaded.Provider);
    Assert.Null(reloaded.PromptPricePerToken);
    Assert.Null(reloaded.PromptCacheReadPricePerToken);
    Assert.Null(reloaded.CompletionPricePerToken);
    // Issue #290: the two model ids are members added after this payload was written, so
    // they read back null - the same legacy rule the #22 members follow. A row that cannot
    // name what served it must not be given a name after the fact.
    Assert.Null(reloaded.RequestedModelId);
    Assert.Null(reloaded.ServedModelId);
    Assert.Same(UsageCallClass.MainLoop, reloaded.EffectiveCallClass);
  }

  [Fact]
  public async Task AppendEventAsync_UsageCounts_AreReadableFromTheDatabaseDirectly()
  {
    // The acceptance criterion 'computable from persisted data alone' - read it the way an
    // eval or a human would, with plain SQL over the event table, not through the store API.
    AgentId id = AgentId.NewId();
    _ = await _store.AppendEventAsync(
        new AgentUsageRecorded(id, DateTimeOffset.UtcNow, Guid.NewGuid(), 0, 1000, 200, 750, 55),
        ct: TestContext.Current.CancellationToken);

    using Microsoft.Data.Sqlite.SqliteConnection connection = new AppDatabase(_dbPath).OpenReadOnly();
    using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
    command.CommandText = """
      SELECT type, payload_json FROM agent_events WHERE agent_id=@id;
      """;
    _ = command.Parameters.AddWithValue("@id", id.ToString());
    using Microsoft.Data.Sqlite.SqliteDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
    Assert.Equal("usage", reader.GetString(0));
    string payload = reader.GetString(1);
    Assert.Contains("\"ReasoningTokens\":55", payload, StringComparison.Ordinal);
    Assert.Contains("\"CachedInputTokens\":750", payload, StringComparison.Ordinal);
  }
}
