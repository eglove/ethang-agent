using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

public sealed class SqliteSelfDatabaseAccessTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(
      Path.GetTempPath(), $"ethang-selfdb-{Guid.NewGuid():N}.db");
  private readonly AppDatabase _database;
  private readonly SqliteSelfDatabaseAccess _access;

  public SqliteSelfDatabaseAccessTests()
  {
    _database = new AppDatabase(_dbPath);
    _access = new SqliteSelfDatabaseAccess(_database);
  }

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

  // ---- Describe (db_schema) ----

  [Fact]
  public async Task Describe_ListsMigratedTables_AndHidesInternalOnes()
  {
    Result<SelfDatabaseSchema> schema = await _access.DescribeAsync(false, ct: TestContext.Current.CancellationToken);

    Assert.True(schema.IsSuccess);
    SelfDatabaseSchema s = schema.Value;
    string[] names = [.. s.Objects.Select(o => o.Name)];
    Assert.Contains("state_keys", names);
    Assert.Contains("agents", names);
    Assert.Contains("agent_messages", names);
    Assert.Contains("curated_memories", names);
    // The FTS5 index itself is meaningful; its shadow tables are not.
    Assert.Contains("curated_memories_fts", names);
    Assert.Contains("state_keys_fts", names);
    Assert.DoesNotContain("curated_memories_fts_data", names);
    Assert.DoesNotContain("curated_memories_fts_config", names);
    Assert.DoesNotContain("state_keys_fts_content", names);
    Assert.DoesNotContain("sqlite_sequence", names);
    Assert.DoesNotContain("sqlite_schema", names);
    Assert.All(s.Objects, o => Assert.False(o.IsView));
  }

  [Fact]
  public async Task Describe_ReportsSchemaVersion()
  {
    Result<SelfDatabaseSchema> schema = await _access.DescribeAsync(false, ct: TestContext.Current.CancellationToken);

    Assert.True(schema.IsSuccess);
    // Bump alongside the next AppDatabase migration.
    Assert.Equal(8, schema.Value.SchemaVersion);
  }

  [Fact]
  public async Task Describe_ColumnsCarryTypePkNotNullAndIndexes()
  {
    Result<SelfDatabaseSchema> schema = await _access.DescribeAsync(false, ct: TestContext.Current.CancellationToken);

    Assert.True(schema.IsSuccess);
    SchemaObject stateKeys = Assert.Single(schema.Value.Objects, o => o.Name == "state_keys");
    Assert.Null(stateKeys.RowCount);
    SchemaColumn workspace = Assert.Single(stateKeys.Columns, c => c.Name == "workspace_id");
    Assert.Equal("TEXT", workspace.Type);
    Assert.True(workspace.NotNull);
    Assert.True(workspace.IsPrimaryKey);
    SchemaColumn value = Assert.Single(stateKeys.Columns, c => c.Name == "value");
    Assert.False(value.IsPrimaryKey);

    SchemaObject transitions = Assert.Single(schema.Value.Objects, o => o.Name == "transitions");
    SchemaIndex status = Assert.Single(transitions.Indexes, i => i.Name == "ix_transitions_ws_status");
    Assert.False(status.IsUnique);
    Assert.Equal(["workspace_id", "status"], status.Columns);

    // The composite PRIMARY KEY of agent_messages backs a unique autoindex.
    SchemaObject messages = Assert.Single(schema.Value.Objects, o => o.Name == "agent_messages");
    SchemaIndex autoindex = Assert.Single(messages.Indexes, i => i.Name.StartsWith("sqlite_autoindex", StringComparison.Ordinal));
    Assert.True(autoindex.IsUnique);
    Assert.Equal(["agent_id", "seq"], autoindex.Columns);
  }

  [Fact]
  public async Task Describe_IncludeCounts_TakesRowCounts()
  {
    await InsertPreferencesAsync(3);

    Result<SelfDatabaseSchema> schema = await _access.DescribeAsync(true, ct: TestContext.Current.CancellationToken);

    Assert.True(schema.IsSuccess);
    SchemaObject preferences = Assert.Single(schema.Value.Objects, o => o.Name == "app_preferences");
    Assert.Equal(3, preferences.RowCount);
  }

  [Fact]
  public async Task Describe_DefaultOmitsCounts()
  {
    await InsertPreferencesAsync(3);

    Result<SelfDatabaseSchema> schema = await _access.DescribeAsync(false, ct: TestContext.Current.CancellationToken);

    Assert.True(schema.IsSuccess);
    SchemaObject preferences = Assert.Single(schema.Value.Objects, o => o.Name == "app_preferences");
    Assert.Null(preferences.RowCount);
  }

  // ---- Query (db_query) ----

  [Fact]
  public async Task Query_ReturnsColumnsAndRows()
  {
    Result<SelfQueryResult> result = await _access.QueryAsync(
        "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'agents' ORDER BY name", 10, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(["name"], result.Value.Columns);
    _ = Assert.Single(result.Value.Rows);
    Assert.Equal("agents", result.Value.Rows[0][0].Text);
    Assert.False(result.Value.Truncated);
  }

  [Fact]
  public async Task Query_TruncatesAtMaxRows_AndReportsIt()
  {
    await InsertPreferencesAsync(5);

    Result<SelfQueryResult> capped = await _access.QueryAsync(
        "SELECT key FROM app_preferences ORDER BY key", 3, ct: TestContext.Current.CancellationToken);
    Result<SelfQueryResult> exact = await _access.QueryAsync(
        "SELECT key FROM app_preferences ORDER BY key", 5, ct: TestContext.Current.CancellationToken);

    Assert.True(capped.IsSuccess);
    Assert.Equal(3, capped.Value.Rows.Count);
    Assert.True(capped.Value.Truncated);
    Assert.True(exact.IsSuccess);
    Assert.Equal(5, exact.Value.Rows.Count);
    Assert.False(exact.Value.Truncated);
  }

  [Fact]
  public async Task Query_WritesFail_OnTheReadOnlyConnection()
  {
    Result<SelfQueryResult> result = await _access.QueryAsync("CREATE TABLE hack (id INTEGER)", 10, ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("QueryFailed", result.Error.Code);
    Assert.Contains("readonly", result.Error.Message, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task Query_WritableCteForm_Fails_OnTheReadOnlyConnection()
  {
    Result<SelfQueryResult> result = await _access.QueryAsync(
        "WITH d AS (SELECT 'k' AS key) INSERT INTO app_preferences (key, value, updated_at) " +
        "SELECT key, 'v', 't' FROM d", 10, ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("QueryFailed", result.Error.Code);
    Assert.Contains("readonly", result.Error.Message, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task Query_CarriesNullsBlobsAndText_ThroughTypedCells()
  {
    Result<SelfQueryResult> result = await _access.QueryAsync(
        "SELECT NULL AS n, randomblob(4) AS b, 'a|b' AS pipe, char(10) AS nl, 42 AS num", 10, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    IReadOnlyList<SelfQueryCell> row = result.Value.Rows[0];
    Assert.Equal(SelfQueryCell.Null, row[0]);
    Assert.Null(row[1].Text);
    Assert.Equal(4, row[1].BlobByteCount);
    Assert.Equal("a|b", row[2].Text);
    Assert.Equal("\n", row[3].Text);
    Assert.Equal("42", row[4].Text);
  }

  [Fact]
  public async Task Query_UnknownTable_FailsWithQueryFailed()
  {
    Result<SelfQueryResult> result = await _access.QueryAsync("SELECT * FROM nope", 10, ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("QueryFailed", result.Error.Code);
    Assert.Contains("no such table", result.Error.Message, StringComparison.Ordinal);
  }

  // ---- helpers ----

  private async Task InsertPreferencesAsync(int count)
  {
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
    for (int i = 0; i < count; i++)
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteCommand command = connection.CreateCommand();
#pragma warning restore CA2007
      command.CommandText = "INSERT INTO app_preferences (key, value, updated_at) VALUES (@k, 'v', 't');";
      _ = command.Parameters.AddWithValue("@k", $"key_{i}");
      _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
  }
}
