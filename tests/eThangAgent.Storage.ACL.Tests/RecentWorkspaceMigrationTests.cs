using eThangAgent.TestSupport;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>V47 adds <c>recent_workspaces</c>: the launcher's list of directories the user has
///     opened, newest first. The table stores the normalized root and the moment it was last
///     opened, and nothing else — a workspace IS its path, so there is no identity to mint and
///     no marker to write into the user's directory.</summary>
public sealed class RecentWorkspaceMigrationTests : IDisposable
{
  private readonly string _dbPath = TemplateDatabase.Copy();

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

  private void Execute(string sql)
  {
    using SqliteConnection connection = new($"Data Source={_dbPath}");
    connection.Open();
    using SqliteCommand command = connection.CreateCommand();
    // Named decision (CA2100): every caller passes a literal from this test file.
#pragma warning disable CA2100 // Review SQL query for security vulnerabilities
    command.CommandText = sql;
#pragma warning restore CA2100
    _ = command.ExecuteNonQuery();
  }

  private T Scalar<T>(string sql)
  {
    using SqliteConnection connection = new($"Data Source={_dbPath}");
    connection.Open();
    using SqliteCommand command = connection.CreateCommand();
#pragma warning disable CA2100 // Review SQL query for security vulnerabilities
    command.CommandText = sql;
#pragma warning restore CA2100
    object value = command.ExecuteScalar()
        ?? throw new InvalidOperationException("the query returned no value");
    return (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
  }

  /// <summary>Builds a fully-migrated database, drops V47's table and stamps user_version 46 —
  ///     the exact pre-V47 state a shipped database is in.</summary>
  private void SeedV46()
  {
    _ = new AppDatabase(_dbPath);
    Execute("DROP TABLE IF EXISTS recent_workspaces; PRAGMA user_version = 46;");
  }

  [Fact]
  public void Migration_CreatesTheRecentWorkspaceTable()
  {
    SeedV46();
    _ = new AppDatabase(_dbPath);

    Assert.Equal(AppDatabase.LatestSchemaVersion,
        Scalar<long>("PRAGMA user_version;"));
    Assert.Equal(1, Scalar<long>(
        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='recent_workspaces';"));
    Assert.Equal(0, Scalar<long>("SELECT COUNT(*) FROM recent_workspaces;"));
  }

  [Fact]
  public void Migration_KeepsExistingRows()
  {
    _ = new AppDatabase(_dbPath);
    Execute("DROP TABLE IF EXISTS recent_workspaces; PRAGMA user_version = 46;");
    Execute("INSERT INTO agents (id, depth, status, model_used, task_prompt, created_at) VALUES ('a1', 0, 0, 'm', 'p', '2026-01-01T00:00:00Z');");

    _ = new AppDatabase(_dbPath);

    Assert.Equal(1, Scalar<long>("SELECT COUNT(*) FROM agents WHERE id='a1';"));
  }

  [Fact]
  public void Migration_IsIdempotent()
  {
    SeedV46();
    _ = new AppDatabase(_dbPath);
    Execute("INSERT INTO recent_workspaces (root, opened_at) VALUES ('C:\\work\\alpha', '2026-01-01T00:00:00Z');");

    _ = new AppDatabase(_dbPath);

    Assert.Equal(1, Scalar<long>("SELECT COUNT(*) FROM recent_workspaces;"));
  }

  [Fact]
  public void Table_RefusesADuplicateRoot()
  {
    _ = new AppDatabase(_dbPath);
    Execute("INSERT INTO recent_workspaces (root, opened_at) VALUES ('C:\\work\\alpha', '2026-01-01T00:00:00Z');");

    _ = Assert.Throws<SqliteException>(() => Execute(
        "INSERT INTO recent_workspaces (root, opened_at) VALUES ('C:\\work\\alpha', '2026-01-02T00:00:00Z');"));
  }

  [Fact]
  public void Table_RefusesANullOpenedAt()
  {
    _ = new AppDatabase(_dbPath);

    _ = Assert.Throws<SqliteException>(() => Execute(
        "INSERT INTO recent_workspaces (root, opened_at) VALUES ('C:\\work\\alpha', NULL);"));
  }
}
