using eThangAgent.PlanDomain;
using eThangAgent.SharedKernel;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Issue #279: one unreadable plan row must not blank the whole workspace
///     catalog. The listing SKIPS the affected plan and REPORTS it (a CorruptPlanRow
///     naming the plan and the reason) so the caller can render a marker instead of an
///     exception; GetAsync on that one plan still fails, because a single plan has no
///     blast radius to contain.
///     <para>The corrupt rows are seeded into a LEGACY-shaped schema (no CHECK constraints),
///     because V20's constraints make this corruption unwritable going forward - which is
///     the other half of the fix, pinned separately below.</para></summary>
public sealed class SqlitePlanStoreCorruptRowTests : IDisposable
{
  private const string Workspace = "C:\\ws\\alpha";
  private readonly string _dbPath = Path.Combine(
      Path.GetTempPath(), $"ethang-plancorrupt-{Guid.NewGuid():N}.db");
  private readonly AppDatabase _database;
  private readonly SqlitePlanStore _store;

  public SqlitePlanStoreCorruptRowTests()
  {
    _database = new AppDatabase(_dbPath);
    _store = new SqlitePlanStore(_database, Workspace);
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

  /// <summary>Rebuilds plans/plan_steps WITHOUT the V20 CHECK constraints, reproducing the
  ///     schema a pre-V20 database had - the only state in which a corrupt status can exist.
  ///     Rows are preserved.</summary>
  private void MakeLegacySchema()
  {
    using SqliteConnection connection = _database.Open();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = """
        CREATE TABLE plans_legacy (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            workspace_id TEXT NOT NULL,
            session_id TEXT NOT NULL,
            title TEXT NOT NULL,
            goal TEXT NOT NULL,
            status TEXT NOT NULL,
            version INTEGER NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        INSERT INTO plans_legacy SELECT id, workspace_id, session_id, title, goal, status, version, created_at, updated_at FROM plans;
        DROP TABLE plans;
        ALTER TABLE plans_legacy RENAME TO plans;
        CREATE TABLE plan_steps_legacy (
            plan_id INTEGER NOT NULL REFERENCES plans(id),
            position INTEGER NOT NULL,
            title TEXT NOT NULL,
            detail TEXT,
            todo_id INTEGER,
            status TEXT NOT NULL,
            PRIMARY KEY (plan_id, position)
        );
        INSERT INTO plan_steps_legacy SELECT plan_id, position, title, detail, todo_id, status FROM plan_steps;
        DROP TABLE plan_steps;
        ALTER TABLE plan_steps_legacy RENAME TO plan_steps;
        """;
    _ = command.ExecuteNonQuery();
  }

  /// <summary>Writes a plan + steps straight to SQL, bypassing the capability provider's
  ///     validation - the exact path that produced the corrupt rows in #279.</summary>
  private void InsertRawPlan(int id, string planStatus, params string[] stepStatuses)
  {
    using SqliteConnection connection = _database.Open();
    using (SqliteCommand plan = connection.CreateCommand())
    {
      plan.CommandText = """
          INSERT INTO plans (id, workspace_id, session_id, title, goal, status, version, created_at, updated_at)
          VALUES (@id, @ws, 'sess-x', @title, 'g', @status, 1, @at, @at);
          """;
      _ = plan.Parameters.AddWithValue("@id", id);
      _ = plan.Parameters.AddWithValue("@ws", Workspace);
      _ = plan.Parameters.AddWithValue("@title", $"plan {id}");
      _ = plan.Parameters.AddWithValue("@status", planStatus);
      _ = plan.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
      _ = plan.ExecuteNonQuery();
    }

    for (int i = 0; i < stepStatuses.Length; i++)
    {
      using SqliteCommand step = connection.CreateCommand();
      step.CommandText = "INSERT INTO plan_steps (plan_id, position, title, detail, todo_id, status) VALUES (@id, @pos, @title, NULL, NULL, @status);";
      _ = step.Parameters.AddWithValue("@id", id);
      _ = step.Parameters.AddWithValue("@pos", i + 1);
      _ = step.Parameters.AddWithValue("@title", $"step {i + 1}");
      _ = step.Parameters.AddWithValue("@status", stepStatuses[i]);
      _ = step.ExecuteNonQuery();
    }
  }

  [Fact]
  public async Task ListAsync_OneCorruptStepRow_StillListsHealthyPlans_AndReportsTheCorruptOne()
  {
    MakeLegacySchema();
    InsertRawPlan(1, "Active", "Pending", "Done");
    InsertRawPlan(2, "Active", "Completed"); // not a PlanStepStatus member
    InsertRawPlan(3, "Active", "InProgress");

    Result<PlanCatalog> listed = await _store.ListAsync(null, TestContext.Current.CancellationToken);

    Assert.True(listed.IsSuccess, listed.Error?.Message ?? "the listing must not fail");
    Assert.Equal([1, 3], listed.Value.Plans.Select(p => p.Id));
    CorruptPlanRow corrupt = Assert.Single(listed.Value.Corrupt);
    Assert.Equal(2, corrupt.PlanId);
    Assert.Contains("Completed", corrupt.Reason, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ListAsync_UnknownPlanStatus_ReportsThatPlanToo()
  {
    MakeLegacySchema();
    InsertRawPlan(1, "Active", "Pending");
    InsertRawPlan(2, "NotAStatus", "Pending");

    Result<PlanCatalog> listed = await _store.ListAsync(null, TestContext.Current.CancellationToken);

    Assert.True(listed.IsSuccess, listed.Error?.Message ?? "the listing must not fail");
    _ = Assert.Single(listed.Value.Plans);
    Assert.Equal(2, Assert.Single(listed.Value.Corrupt).PlanId);
  }

  [Fact]
  public async Task ListAsync_NoCorruptRows_ReportsAnEmptyCorruptList()
  {
    InsertRawPlan(1, "Active", "Pending", "InProgress", "Done");

    Result<PlanCatalog> listed = await _store.ListAsync(null, TestContext.Current.CancellationToken);

    Assert.True(listed.IsSuccess, listed.Error?.Message ?? "expected success");
    _ = Assert.Single(listed.Value.Plans);
    Assert.Empty(listed.Value.Corrupt);
  }

  [Fact]
  public async Task GetAsync_CorruptPlan_FailsWithNamedCode_InsteadOfThrowing()
  {
    MakeLegacySchema();
    InsertRawPlan(7, "Active", "Completed");

    Result<Plan> got = await _store.GetAsync(7, TestContext.Current.CancellationToken);

    Assert.False(got.IsSuccess);
    Assert.Equal("CorruptPlanRow", got.Error.Code);
  }

  [Fact]
  public async Task ListAsync_NumericStepStatus_IsTreatedAsCorrupt_NotParsedAsAnEnumValue()
  {
    // Enum.TryParse accepts '5' and yields an UNDEFINED member; a status must be a
    // NAME that maps to a defined member or it is corrupt.
    MakeLegacySchema();
    InsertRawPlan(1, "Active", "5");

    Result<PlanCatalog> listed = await _store.ListAsync(null, TestContext.Current.CancellationToken);

    Assert.True(listed.IsSuccess, listed.Error?.Message ?? "the listing must not fail");
    Assert.Empty(listed.Value.Plans);
    Assert.Equal(1, Assert.Single(listed.Value.Corrupt).PlanId);
  }

  [Fact]
  public void V20_CheckConstraint_RefusesAnOutOfEnumStepStatus()
  {
    // The other half of #279: the capability provider already refuses bad input, but
    // nothing stopped a DIRECT writer (an exec script's SQL). Now SQLite itself does.
    using SqliteConnection connection = _database.Open();
    using SqliteCommand plan = connection.CreateCommand();
    plan.CommandText = "INSERT INTO plans (workspace_id, session_id, title, goal, status, version, created_at, updated_at) VALUES (@ws, 's', 't', 'g', 'Active', 1, @at, @at);";
    _ = plan.Parameters.AddWithValue("@ws", Workspace);
    _ = plan.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
    _ = plan.ExecuteNonQuery();

    using SqliteCommand step = connection.CreateCommand();
    step.CommandText = "INSERT INTO plan_steps (plan_id, position, title, detail, todo_id, status) VALUES (1, 1, 'x', NULL, NULL, 'Completed');";
    _ = Assert.Throws<SqliteException>(() => step.ExecuteNonQuery());
  }
}
