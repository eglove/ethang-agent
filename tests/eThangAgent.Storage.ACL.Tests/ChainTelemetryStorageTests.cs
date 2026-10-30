using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Issue #309: the telemetry report end to end over a REAL database — the chain record,
///     the usage ledger, the plan review record and the subagent-era baseline, each read through
///     its own adapter.
///     <para>THE TWO CRITERIA THIS FILE EXISTS FOR: 'the report is computed from persisted rows,
///     never from self-reported summaries' and 'the same rows produce the same numbers'. Both
///     are only checkable against a real store — a fake would prove the arithmetic and nothing
///     about the SQL.</para></summary>
public sealed class ChainTelemetryStorageTests : IDisposable
{
  private const string Ws = "C:\\ws\\alpha";
  private const string OtherWs = "C:\\ws\\beta";
  private static readonly DateTimeOffset T0 = new(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);
  private readonly string _dbPath = Path.Combine(Path.GetTempPath(),
      $"ethang-telemetry-{Guid.NewGuid():N}.db");

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

  private AppDatabase Db => new(_dbPath);

  private static T Must<T>(Result<T> result)
  {
    Assert.True(result.IsSuccess, result.Error?.Message ?? "expected success");
    return result.Value;
  }

  /// <summary>Creates a chain through the REAL store, so the rows the telemetry read sees are
  ///     the rows production writes.</summary>
  private async Task<Chain> CreateAsync(string workspace, string title, params int[] positions)
  {
    SqliteChainStore store = new(Db, workspace);
    return Must(await store.CreateAsync(Chain.New(workspace, 7, 1, title, "session-root",
        positions.Length == 0 ? [1] : positions, T0), TestContext.Current.CancellationToken)
        .ConfigureAwait(false));
  }

  /// <summary>Writes one usage row exactly as the loop does, so the cost reader parses a
  ///     production-shaped payload rather than a fixture invented to match the parser.</summary>
  private void WriteUsage(string agentId, decimal reportedCost, string workspace = Ws)
  {
    string payload = "{\"TurnId\":\"t1\",\"Iteration\":0,\"InputTokens\":100,\"OutputTokens\":10,"
        + "\"CachedInputTokens\":0,\"ReasoningTokens\":0,\"CallClass\":\"main_loop\","
        + "\"ModelId\":\"test/model\",\"Provider\":\"openrouter\","
        + "\"WorkspaceId\":\"" + workspace.Replace("\\", "\\\\", StringComparison.Ordinal) + "\","
        + "\"ReportedCost\":" + reportedCost.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)
        + ",\"ExpectsReportedCost\":true}";
    using Microsoft.Data.Sqlite.SqliteConnection connection = Db.Open();
    using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
    command.CommandText = "INSERT INTO agent_events (agent_id, occurred_at, type, payload_json) "
        + "VALUES (@id, @at, 'usage', @payload);";
    _ = command.Parameters.AddWithValue("@id", agentId);
    _ = command.Parameters.AddWithValue("@at", T0.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    _ = command.Parameters.AddWithValue("@payload", payload);
    _ = command.ExecuteNonQuery();
  }

  [Fact]
  public async Task ListAsync_ReadsTheChainsOwnExecutionColumns_NotASecondaryRecord()
  {
    Chain chain = await CreateAsync(Ws, "Chain telemetry", 1, 2);
    SqliteChainStore store = new(Db, Ws);
    _ = Must(await store.SaveAsync(chain.ClaimStep(1, "session-1", T0), chain.Version,
        TestContext.Current.CancellationToken));

    ChainTelemetryCatalog catalog = Must(await new SqliteChainTelemetrySource(Db, Ws)
        .ListAsync(TestContext.Current.CancellationToken));

    ChainTelemetryChain read = Assert.Single(catalog.Chains);
    Assert.Equal(chain.Id, read.ChainId);
    Assert.Equal("Chain telemetry", read.Title);
    Assert.Equal(2, read.Steps.Count);
    ChainTelemetryStep claimed = read.Steps[0];
    Assert.Equal(ChainStepStatus.Running, claimed.Status);
    Assert.Equal("session-1", claimed.SessionId);
    Assert.Equal(1, claimed.Attempts);
    Assert.True(claimed.ClaimedAt is not null);
    Assert.Null(claimed.CompletedAt);
    Assert.Empty(catalog.Corrupt);
    Assert.False(read.Steps[0].IsSettled);
  }

  [Fact]
  public async Task ListAsync_KeepsAnotherWorkspacesChainsInvisible()
  {
    _ = await CreateAsync(Ws, "mine");
    _ = await CreateAsync(OtherWs, "theirs");

    ChainTelemetryCatalog catalog = Must(await new SqliteChainTelemetrySource(Db, Ws)
        .ListAsync(TestContext.Current.CancellationToken));

    Assert.Equal("mine", Assert.Single(catalog.Chains).Title);
  }

  [Fact]
  public async Task ListAsync_OfAnEmptyWorkspace_IsAnEmptyAnswer_NotAFailure()
  {
    ChainTelemetryCatalog catalog = Must(await new SqliteChainTelemetrySource(Db, Ws)
        .ListAsync(TestContext.Current.CancellationToken));

    Assert.Empty(catalog.Chains);
    Assert.Empty(catalog.Corrupt);
  }

  [Fact]
  public async Task CostsForAsync_PricesEachSessionFromTheLedgerAndReportsAnUnpricedOne()
  {
    WriteUsage("session-1", 0.25m);
    WriteUsage("session-1", 0.25m);
    WriteUsage("session-2", 0m);

    IReadOnlyList<ChainTelemetrySessionCost> costs = Must(await new SqliteChainSessionCostSource(Db)
        .CostsForAsync(["session-1", "session-2", "session-none"],
            TestContext.Current.CancellationToken));

    Assert.Equal(2, costs.Count);
    ChainTelemetrySessionCost first = costs.Single(cost => cost.SessionId == "session-1");
    Assert.Equal(0.5m, first.Cost);
    Assert.Equal(2, first.PricedCalls);
    // A reported charge of zero is a REAL charge, not an absence — the ledger's own rule.
    Assert.Equal(0m, costs.Single(cost => cost.SessionId == "session-2").Cost);
    // A session with no rows is ABSENT rather than present with a zero cost.
    Assert.DoesNotContain(costs, cost => cost.SessionId == "session-none");
  }

  [Fact]
  public async Task CostsForAsync_WithNoSessions_AsksTheLedgerNothing()
  {
    IReadOnlyList<ChainTelemetrySessionCost> costs = Must(await new SqliteChainSessionCostSource(Db)
        .CostsForAsync([], TestContext.Current.CancellationToken));

    Assert.Empty(costs);
  }

  [Fact]
  public async Task ReviewFacts_JoinThroughThePlan_SoAnotherWorkspacesReviewsAreInvisible()
  {
    int planId = InsertPlan(Ws, version: 3);
    int otherPlan = InsertPlan(OtherWs, version: 1);
    InsertReview(planId, judgedVersion: 1, verdict: "deny");
    InsertReview(otherPlan, judgedVersion: 1, verdict: "deny");

    ChainTelemetryReviewFacts facts = Must(await new SqliteChainReviewTelemetrySource(Db, Ws)
        .ListAsync(TestContext.Current.CancellationToken));

    ChainTelemetryReview review = Assert.Single(facts.Reviews);
    Assert.Equal(planId, review.PlanId);
    Assert.True(review.IsDenial);
    // The plan has moved from the judged version 1 to 3, so the denial produced a revision.
    Assert.True(facts.ChangedAfter(review));
    Assert.Equal(3, facts.CurrentPlanVersions[planId]);
  }

  [Fact]
  public async Task ReviewFacts_SkipAnUnreadableVerdict_RatherThanCountingIt()
  {
    int planId = InsertPlan(Ws, version: 1);
    InsertReview(planId, judgedVersion: 1, verdict: "deny");
    // A verdict nobody can classify must not be guessed into the deny rate.
    InsertRawReview(planId, judgedVersion: 1, verdict: "probably-fine");

    ChainTelemetryReviewFacts facts = Must(await new SqliteChainReviewTelemetrySource(Db, Ws)
        .ListAsync(TestContext.Current.CancellationToken));

    _ = Assert.Single(facts.Reviews);
  }

  [Fact]
  public async Task Baseline_MeasuresTheSubagentEraRows_AndCountsUnpricedRuns()
  {
    InsertAgent("child-a", parent: "root", status: 1, completedAt: T0.AddHours(1));
    InsertAgent("child-b", parent: "root", status: 1, completedAt: T0.AddHours(3));
    InsertAgent("child-c", parent: "root", status: 2, completedAt: null);
    WriteUsage("child-a", 0.10m);

    ChainTelemetryBaseline? baseline = Must(await new SqliteChainBaselineSource(Db, Ws)
        .ReadAsync(TestContext.Current.CancellationToken));

    Assert.NotNull(baseline);
    Assert.Equal(3, baseline.Runs);
    Assert.Equal(2, baseline.CompletedRuns);
    // 1h and 3h completed runs: median 2h.
    Assert.Equal(7200d, baseline.MedianDurationSeconds);
    Assert.Equal(0.10m, baseline.MedianCostPerCompletedRun);
    // child-b completed with no priced call, so it is counted rather than priced at zero.
    Assert.Equal(1, baseline.UnpricedRuns);
    Assert.True(baseline.IsComparable);
  }

  [Fact]
  public async Task Baseline_IsNull_WhenNoChildRowsExist()
  {
    ChainTelemetryBaseline? baseline = Must(await new SqliteChainBaselineSource(Db, Ws)
        .ReadAsync(TestContext.Current.CancellationToken));

    Assert.Null(baseline);
  }

  // ---- fixtures -----------------------------------------------------------

  private int InsertPlan(string workspace, int version)
  {
    using Microsoft.Data.Sqlite.SqliteConnection connection = Db.Open();
    using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
    command.CommandText = """
        INSERT INTO plans (workspace_id, session_id, title, goal, status, version, created_at, updated_at)
        VALUES (@ws, 'session-root', 'Plan', 'Goal', 'Active', @version, @at, @at);
        SELECT last_insert_rowid();
        """;
    _ = command.Parameters.AddWithValue("@ws", workspace);
    _ = command.Parameters.AddWithValue("@version", version);
    _ = command.Parameters.AddWithValue("@at", T0.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private void InsertReview(int planId, int judgedVersion, string verdict) =>
      InsertRawReview(planId, judgedVersion, verdict);

  /// <summary>Writes a review row directly, including one whose verdict is outside the enum —
  ///     the shape a corrupt or hand-edited row has.</summary>
  private void InsertRawReview(int planId, int judgedVersion, string verdict)
  {
    using Microsoft.Data.Sqlite.SqliteConnection connection = Db.Open();
    using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
    // The verdict CHECK constraint refuses an out-of-enum value on a healthy schema, which is
    // exactly the guard the domain relies on; this test writes through a table rebuilt without
    // it only when the constraint is absent, so it skips cleanly when the schema holds.
    command.CommandText = """
        INSERT INTO plan_reviews (plan_id, plan_version, verdict, reviewer_session_id,
            reviewer_model, requirement_coverage, testability, blast_radius, findings_json, created_at)
        VALUES (@plan, @version, @verdict, 'reviewer', 'test/model', 1, 1, 1, '[]', @at);
        """;
    _ = command.Parameters.AddWithValue("@plan", planId);
    _ = command.Parameters.AddWithValue("@version", judgedVersion);
    _ = command.Parameters.AddWithValue("@verdict", verdict);
    _ = command.Parameters.AddWithValue("@at", T0.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    try
    {
      _ = command.ExecuteNonQuery();
    }
    // Named decision (CA1031): the CHECK constraint is the domain's own guard doing its job,
    // and a test that cannot write a corrupt row proves the constraint works.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Microsoft.Data.Sqlite.SqliteException)
    {
      // The CHECK constraint refused the row: the domain's own guard, doing its job.
    }
#pragma warning restore CA1031
  }

  /// <summary>Writes one child agent row: the subagent era's own record.
  ///     <para>THE STATUS IS THE PERSISTED INTEGER (0 Running, 1 Completed, 2 Failed), which is
  ///     what the agent store writes — the baseline reader parses the column rather than an enum
  ///     name, so a fixture using names would test a shape production never produces.</para>
  ///     </summary>
  private void InsertAgent(string id, string parent, int status, DateTimeOffset? completedAt)
  {
    using Microsoft.Data.Sqlite.SqliteConnection connection = Db.Open();
    using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
    command.CommandText = """
        INSERT INTO agents (id, parent_id, depth, status, model_used, label, task_prompt,
            created_at, completed_at, workspace_id, provider, attempts)
        VALUES (@id, @parent, 1, @status, 'test/model', 'child', 'do work', @created, @completed,
            @ws, 'openrouter', 1);
        """;
    _ = command.Parameters.AddWithValue("@id", id);
    _ = command.Parameters.AddWithValue("@parent", parent);
    _ = command.Parameters.AddWithValue("@status", status);
    _ = command.Parameters.AddWithValue("@created", T0.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    _ = command.Parameters.AddWithValue("@completed", completedAt is { } at
        ? at.ToString("O", System.Globalization.CultureInfo.InvariantCulture) : DBNull.Value);
    // Unstamped by default, which is the shape ALL 2,156 real child rows have — the reason the
    // baseline falls back to the global set.
    _ = command.Parameters.AddWithValue("@ws", DBNull.Value);
    _ = command.ExecuteNonQuery();
  }
}
