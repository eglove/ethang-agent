using eThangAgent.TestSupport;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Migration V40: agent-authored skills are gone, so the learned-skill tables are
///     dropped. A database that still holds them upgrades in place, loses exactly that state,
///     and keeps every other table and row; a FRESH database never creates them at all, and
///     re-opening is a no-op.</summary>
public sealed class LearnedSkillRemovalMigrationTests : IDisposable
{
  /// <summary>The schema version immediately BEFORE V40. A literal, because the version
  ///     before a given migration is a fact about THAT migration and migrations are never
  ///     renumbered; 'latest minus one' would stop being the pre-V40 state at the next
  ///     migration.</summary>
  private const int VersionBeforeV40 = 39;

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
    catch
    {
    }
#pragma warning restore CA1031, S108
  }

  /// <summary>Recreates the learned-skill shape V3 used to create, seeds a row into each
  ///     table, and stamps user_version 39 - the exact pre-V40 state.</summary>
  private void SeedV39Shape()
  {
    _ = new AppDatabase(_dbPath);
    Execute(string.Join('\n',
        "CREATE TABLE learned_skills (",
        "    name TEXT PRIMARY KEY,",
        "    description TEXT NOT NULL,",
        "    body TEXT NOT NULL,",
        "    version INTEGER NOT NULL,",
        "    provenance_session TEXT NULL,",
        "    created_at TEXT NOT NULL,",
        "    updated_at TEXT NOT NULL",
        ");",
        "CREATE TABLE skill_versions (",
        "    name TEXT NOT NULL,",
        "    version INTEGER NOT NULL,",
        "    description TEXT NOT NULL,",
        "    body TEXT NOT NULL,",
        "    created_at TEXT NOT NULL,",
        "    PRIMARY KEY (name, version)",
        ");",
        "CREATE TABLE skill_usage (",
        "    id INTEGER PRIMARY KEY AUTOINCREMENT,",
        "    skill_name TEXT NOT NULL,",
        "    viewed_at TEXT NOT NULL",
        ");",
        "CREATE INDEX ix_skill_usage_name ON skill_usage (skill_name);",
        "INSERT INTO learned_skills VALUES ('stale', 'd', 'b', 1, NULL, 't', 't');",
        "INSERT INTO skill_versions VALUES ('stale', 1, 'd', 'b', 't');",
        "INSERT INTO skill_usage (skill_name, viewed_at) VALUES ('stale', 't');",
        $"PRAGMA user_version = {VersionBeforeV40};"));
  }

  [Fact]
  public void V40_DropsLearnedTables_And_Leaves_Other_Tables_Untouched()
  {
    SeedV39Shape();

    _ = new AppDatabase(_dbPath); // constructor migrates

    using SqliteConnection connection = Open();
    Assert.Equal(AppDatabase.LatestSchemaVersion, Version(connection));
    Assert.Equal(0L, Scalar(connection,
        "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('learned_skills','skill_versions','skill_usage','ix_skill_usage_name');"));
    Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='state_keys';"));
    Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='plans';"));
  }

  [Fact]
  public void V40_Is_Idempotent_Reopening_Is_A_NoOp()
  {
    SeedV39Shape();

    _ = new AppDatabase(_dbPath); // migrate once
    _ = new AppDatabase(_dbPath); // reopen: no-op

    using SqliteConnection connection = Open();
    Assert.Equal(AppDatabase.LatestSchemaVersion, Version(connection));
    Assert.Equal(0L, Scalar(connection,
        "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('learned_skills','skill_versions','skill_usage');"));
  }

  [Fact]
  public void FreshDatabase_NeverCreatesLearnedTables()
  {
    // The fixture file is itself a freshly migrated database: V3 creates nothing and V40 has
    // nothing to drop, so a database built from scratch never holds a learned-skill table.
    using SqliteConnection connection = Open();
    Assert.Equal(0L, Scalar(connection,
        "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('learned_skills','skill_versions','skill_usage');"));
  }

  private void Execute(string sql)
  {
    using SqliteConnection connection = Open();
    using SqliteCommand command = connection.CreateCommand();
    // Named decision (CA2100): every caller passes a literal declared in this file.
#pragma warning disable CA2100 // Review SQL query for security vulnerabilities
    command.CommandText = sql;
#pragma warning restore CA2100
    _ = command.ExecuteNonQuery();
  }

  private SqliteConnection Open()
  {
    SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
    connection.Open();
    return connection;
  }

  private static long Version(SqliteConnection connection) => Scalar(connection, "PRAGMA user_version;");

  private static long Scalar(SqliteConnection connection, string sql)
  {
    using SqliteCommand command = connection.CreateCommand();
    // Named decision (CA2100): every caller passes a literal declared in this file.
#pragma warning disable CA2100 // Review SQL query for security vulnerabilities
    command.CommandText = sql;
#pragma warning restore CA2100
    return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
  }
}
