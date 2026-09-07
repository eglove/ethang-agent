using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Integration: <see cref="SqliteFileAccess"/> opens REAL SQLite files at a
///     workspace-relative path on a read-only connection. A database with a table
///     answers SELECT; a non-database file fails cleanly (not a database); a missing
///     file fails without creating one (read-only mode never creates).</summary>
public sealed class SqliteFileAccessTests : IDisposable
{
  private readonly string _root = Directory.CreateTempSubdirectory("ethang-sq").FullName;

  [Fact]
  public async Task QueryAsync_ReadsRows_FromARealSqliteFile()
  {
    string path = Path.Combine(_root, "warehouse.db");
    await CreateAsync(path, "CREATE TABLE items (id INTEGER PRIMARY KEY, name TEXT); INSERT INTO items (name) VALUES ('alpha'), ('beta');");

    SqliteFileAccess access = new();
    Result<SelfQueryResult> result = await access.QueryAsync(path, "SELECT name FROM items ORDER BY id", 100, TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess, result.Error?.Message ?? "query failed");
    Assert.Equal(["name"], result.Value.Columns);
    Assert.Equal(2, result.Value.Rows.Count);
    Assert.Equal("alpha", result.Value.Rows[0][0].Text);
    Assert.Equal("beta", result.Value.Rows[1][0].Text);
  }

  [Fact]
  public async Task QueryAsync_OnANonDatabaseFile_FailsCleanly_WithoutThrowing()
  {
    string path = Path.Combine(_root, "notes.txt");
    await File.WriteAllTextAsync(path, "definitely not a database", TestContext.Current.CancellationToken);

    SqliteFileAccess access = new();
    Result<SelfQueryResult> result = await access.QueryAsync(path, "SELECT 1", 100, TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("QueryFailed", result.Error?.Code);
  }

  [Fact]
  public async Task QueryAsync_OnAMissingFile_Fails_WithoutCreatingIt()
  {
    string path = Path.Combine(_root, "gone.db");

    SqliteFileAccess access = new();
    Result<SelfQueryResult> result = await access.QueryAsync(path, "SELECT 1", 100, TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("QueryFailed", result.Error?.Code);
    Assert.False(File.Exists(path), "a read-only connection must never create the file");
  }

  [Fact]
  public async Task QueryAsync_WriteAttempt_Fails_AtTheEngine()
  {
    string path = Path.Combine(_root, "warehouse.db");
    await CreateAsync(path, "CREATE TABLE items (name TEXT);");

    SqliteFileAccess access = new();
    // The lexical gate rejects writes before the connection; a writable-CTE form
    // that slips past it still hits the read-only engine backstop.
    Result<SelfQueryResult> result = await access.QueryAsync(path,
        "WITH x AS (SELECT 1) INSERT INTO items (name) SELECT 'nope' RETURNING name", 100, TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("QueryFailed", result.Error?.Code);
  }

  private static async Task CreateAsync(string path, string setupSql)
  {
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using SqliteConnection setup = new(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
    await setup.OpenAsync(TestContext.Current.CancellationToken);
    await using SqliteCommand create = setup.CreateCommand();
    // Test-fixed setup SQL, never user input.
#pragma warning disable CA2100 // Review SQL query for security vulnerabilities
    create.CommandText = setupSql;
#pragma warning restore CA2100
    _ = await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
#pragma warning restore CA2007
  }

  // Named decision (CA1031): temp-dir cleanup in a test is best effort.
#pragma warning disable CA1031, S108 // Do not catch general exception types
  public void Dispose()
  {
    try
    {
      Directory.Delete(_root, recursive: true);
    }
    catch
    {
    }
    GC.SuppressFinalize(this);
  }
#pragma warning restore CA1031, S108
}
