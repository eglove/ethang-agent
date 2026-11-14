using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;

namespace eThangAgent.Storage.ACL.Tests;

// The reasoning trace's STORE contract (issues #263, #384): the event kind still round-trips
// through the same append-only table, and the migration that removes the rows a build used to
// write leaves every other event kind in place.
public sealed class SqliteAgentStoreReasoningTests : IDisposable
{
  private readonly string _dbPath = TemplateDatabase.Copy();
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
    AgentSpawned spawned = new(id, at, "provider/model", "child");
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

  /// <summary>The migration that bounds the table (V44): every trace this database holds goes,
  ///     every usage row stays, and the record of what was removed is readable afterwards.
  ///     <para>The usage ledger is the half that must survive: three production readers - the
  ///     status bar, the chain session cost source and the chain baseline source - depend on it,
  ///     so a sweep that took a usage row would silently re-price history.</para></summary>
  [Fact]
  public async Task MigrationV44_RemovesEveryReasoningRow_AndKeepsTheUsageLedger()
  {
    AgentId id = AgentId.NewId();
    DateTimeOffset at = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);
    _ = await _store.AppendEventAsync(new AgentReasoningRecorded(id, at, Guid.NewGuid(), 0, "trace"),
        ct: TestContext.Current.CancellationToken);
    _ = await _store.AppendEventAsync(
        new AgentUsageRecorded(id, at.AddSeconds(1), Guid.NewGuid(), 0, 10, 5, null, null),
        ct: TestContext.Current.CancellationToken);

    ReasoningTraceSweepReport report = await Task.Run(() =>
    {
      using Microsoft.Data.Sqlite.SqliteConnection connection = new AppDatabase(_dbPath).Open();
      return ReasoningTraceRetention.Run(connection);
    }, TestContext.Current.CancellationToken);

    Assert.Equal(1, report.RowsRemoved);
    Assert.Equal(1, report.UsageRowsKept);
    Assert.NotNull(report.FirstOccurredAt);
    Assert.Equal(0, report.FreePagesAfter);

    ReasoningTraceSweepReport? recorded = await ReasoningTraceRetention
        .ReadReportAsync(new AppDatabase(_dbPath), TestContext.Current.CancellationToken);
    Assert.NotNull(recorded);
    Assert.Equal(1, recorded.RowsRemoved);

    using Microsoft.Data.Sqlite.SqliteConnection check = new AppDatabase(_dbPath).OpenReadOnly();
    using Microsoft.Data.Sqlite.SqliteCommand command = check.CreateCommand();
    command.CommandText = "SELECT type FROM agent_events ORDER BY id;";
    using Microsoft.Data.Sqlite.SqliteDataReader reader = await command
        .ExecuteReaderAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    List<string> types = [];
    while (await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
    {
      types.Add(reader.GetString(0));
    }

    Assert.Equal(["usage"], types);
  }

  /// <summary>V44 is a migration step, so it runs when a database is BELOW it and never again.
  ///     <para>The step is pinned by version rather than by 'one below the latest': a migration
  ///     added after this one must not quietly turn this test into an exercise of that step.</para>
  ///     </summary>
  [Fact]
  public async Task MigrationV44_RunsOnOpen_FromTheVersionBelowIt()
  {
    const int V43 = 43;
    AgentId id = AgentId.NewId();
    DateTimeOffset at = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);
    _ = await _store.AppendEventAsync(new AgentReasoningRecorded(id, at, Guid.NewGuid(), 0, "trace"),
        ct: TestContext.Current.CancellationToken);
    _ = await _store.AppendEventAsync(
        new AgentUsageRecorded(id, at.AddSeconds(1), Guid.NewGuid(), 0, 1, 1, null, null),
        ct: TestContext.Current.CancellationToken);

    await Step(
        $"PRAGMA user_version = {V43};",
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    _ = new AppDatabase(_dbPath);

    Assert.Equal(AppDatabase.LatestSchemaVersion,
        await Scalar("PRAGMA user_version;", TestContext.Current.CancellationToken).ConfigureAwait(true));
    Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM agent_events WHERE type = 'reasoning';",
        TestContext.Current.CancellationToken).ConfigureAwait(true));
  }
  private async Task Step(string sql, CancellationToken ct)
  {
    using Microsoft.Data.Sqlite.SqliteConnection connection = new AppDatabase(_dbPath).Open();
    using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
    // Named decision (CA2100): the caller passes a literal from this test file.
#pragma warning disable CA2100 // Review SQL query for security vulnerabilities
    command.CommandText = sql;
#pragma warning restore CA2100
    _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(true);
  }

  private async Task<long> Scalar(string sql, CancellationToken ct)
  {
    using Microsoft.Data.Sqlite.SqliteConnection connection = new AppDatabase(_dbPath).OpenReadOnly();
    using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
    // Named decision (CA2100): the caller passes a literal from this test file.
#pragma warning disable CA2100 // Review SQL query for security vulnerabilities
    command.CommandText = sql;
#pragma warning restore CA2100
    return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(true),
        System.Globalization.CultureInfo.InvariantCulture);
  }
}
