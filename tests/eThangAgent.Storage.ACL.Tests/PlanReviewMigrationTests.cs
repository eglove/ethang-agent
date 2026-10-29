using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Issue #216: V27 adds <c>plan_reviews</c>. Pinned end-to-end - a database at the
///     PREVIOUS version upgrades in one pass, the table arrives with its foreign key and its
///     CHECK constraints, and the version stamp is asserted against the CONSTANT rather than
///     a literal every future migration would have to hunt down.</summary>
public sealed class PlanReviewMigrationTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(
      Path.GetTempPath(), $"ethang-planv27-{Guid.NewGuid():N}.db");

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

  /// <summary>Builds a fully-migrated database, drops the V27 table and stamps the version
  ///     BELOW it - the exact pre-V27 state a shipped database is in.
  ///     <para>The stamp is the constant minus one, never a literal: a test that hard-codes
  ///     26 breaks on the next migration for a reason unrelated to what it asserts.</para></summary>
  private void SeedPreviousVersion()
  {
    _ = new AppDatabase(_dbPath);
    Execute($"DROP TABLE IF EXISTS plan_reviews; PRAGMA user_version = {AppDatabase.LatestSchemaVersion - 1};");
  }

  private void Execute(string sql)
  {
    using SqliteConnection connection = new($"Data Source={_dbPath}");
    connection.Open();
    using SqliteCommand command = connection.CreateCommand();
    // Named decision (CA2100): every caller passes a literal or a constant from this file.
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
    return (T)value;
  }

  [Fact]
  public void V27_CreatesTheTable_AndStampsTheLatestVersion()
  {
    SeedPreviousVersion();
    _ = new AppDatabase(_dbPath);

    Assert.Equal(AppDatabase.LatestSchemaVersion, Scalar<long>("PRAGMA user_version;"));
    Assert.Equal(1L, Scalar<long>(
        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='plan_reviews';"));
  }

  [Fact]
  public void V27_UpgradesAnExistingDatabaseInOnePass()
  {
    SeedPreviousVersion();
    // A plan that predates the review table: the migration must not disturb it.
    Execute("""
        INSERT INTO plans (workspace_id, session_id, title, goal, status, version, created_at, updated_at)
            VALUES ('C:\\ws\\alpha', 'sess-planner', 'legacy plan', 'goal', 'Active', 3,
                '2026-10-29T00:00:00.0000000+00:00', '2026-10-29T00:00:00.0000000+00:00');
        """);

    _ = new AppDatabase(_dbPath);

    Assert.Equal(1L, Scalar<long>("SELECT COUNT(*) FROM plans;"));
    Assert.Equal(3L, Scalar<long>("SELECT version FROM plans WHERE id=1;"));
  }

  [Theory]
  [InlineData("approve")]
  [InlineData("deny")]
  [InlineData("approve-with-conditions")]
  public void V27_VerdictColumn_AdmitsEveryAllowedToken(string token)
  {
    _ = new AppDatabase(_dbPath);
    Execute("""
        INSERT INTO plans (workspace_id, session_id, title, goal, status, version, created_at, updated_at)
            VALUES ('C:\\ws\\alpha', 's', 't', 'g', 'Active', 1, '2026-10-29T00:00:00.0000000+00:00',
                '2026-10-29T00:00:00.0000000+00:00');
        """);

    Execute($"""
        INSERT INTO plan_reviews (plan_id, plan_version, verdict, reviewer_session_id,
            reviewer_model, requirement_coverage, testability, blast_radius, findings_json,
            notes, created_at)
            VALUES (1, 1, '{token}', 'sess-r', 'vendor/m', 3, 3, 3, '[]', NULL,
                '2026-10-29T00:00:00.0000000+00:00');
        """);

    Assert.Equal(token, Scalar<string>("SELECT verdict FROM plan_reviews;"));
  }

  [Fact]
  public void V27_VerdictColumn_RefusesAnOutOfEnumValue_FromADirectWriter()
  {
    _ = new AppDatabase(_dbPath);
    Execute("""
        INSERT INTO plans (workspace_id, session_id, title, goal, status, version, created_at, updated_at)
            VALUES ('C:\\ws\\alpha', 's', 't', 'g', 'Active', 1, '2026-10-29T00:00:00.0000000+00:00',
                '2026-10-29T00:00:00.0000000+00:00');
        """);

    // The CHECK constraint is the door the domain's own validation cannot hold shut: an exec
    // script's SQL never passes through PlanReviewVerdictCodec.
    SqliteException refused = Assert.Throws<SqliteException>(() => Execute("""
        INSERT INTO plan_reviews (plan_id, plan_version, verdict, reviewer_session_id,
            reviewer_model, requirement_coverage, testability, blast_radius, findings_json,
            notes, created_at)
            VALUES (1, 1, 'maybe', 'sess-r', 'vendor/m', 3, 3, 3, '[]', NULL,
                '2026-10-29T00:00:00.0000000+00:00');
        """));
    Assert.Contains("CHECK", refused.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void V27_RefusesANegativeRubricScore_FromADirectWriter()
  {
    _ = new AppDatabase(_dbPath);
    Execute("""
        INSERT INTO plans (workspace_id, session_id, title, goal, status, version, created_at, updated_at)
            VALUES ('C:\\ws\\alpha', 's', 't', 'g', 'Active', 1, '2026-10-29T00:00:00.0000000+00:00',
                '2026-10-29T00:00:00.0000000+00:00');
        """);

    SqliteException refused = Assert.Throws<SqliteException>(() => Execute("""
        INSERT INTO plan_reviews (plan_id, plan_version, verdict, reviewer_session_id,
            reviewer_model, requirement_coverage, testability, blast_radius, findings_json,
            notes, created_at)
            VALUES (1, 1, 'approve', 'sess-r', 'vendor/m', -1, 3, 3, '[]', NULL,
                '2026-10-29T00:00:00.0000000+00:00');
        """));
    Assert.Contains("CHECK", refused.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void V27_RefusesAReviewOfAPlanThatDoesNotExist()
  {
    _ = new AppDatabase(_dbPath);

    // The foreign key is why a review is a CHILD of a plan rather than a free-standing log:
    // a review of a plan that does not exist is not a fact about anything.
    SqliteException refused = Assert.Throws<SqliteException>(() => Execute("""
        INSERT INTO plan_reviews (plan_id, plan_version, verdict, reviewer_session_id,
            reviewer_model, requirement_coverage, testability, blast_radius, findings_json,
            notes, created_at)
            VALUES (999, 1, 'approve', 'sess-r', 'vendor/m', 3, 3, 3, '[]', NULL,
                '2026-10-29T00:00:00.0000000+00:00');
        """));
    Assert.Contains("FOREIGN KEY", refused.Message, StringComparison.Ordinal);
  }
}
