using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>The workspace rung of the usage-evidence chain: the MEDIAN shape across every model
///     in the whole ledger that cleared the evidence floor, and the per-pair output tokens the
///     output/input ratio is derived from.
///
///     Anti-tautology: rows are written through the PRODUCTION store
///     (<see cref="SqliteAgentStore.AppendEventAsync"/>) and read through the PRODUCTION read
///     model (<see cref="SqliteUsageQueries"/>), so a payload shape that drifted between the
///     write and read sides fails here rather than in a routing decision nobody re-read.</summary>
public sealed class SqliteUsageQueriesWorkspaceShapeTests : IDisposable
{
  private const string Workspace = "ws-shape";

  private readonly string _dbPath = TemplateDatabase.Copy();
  private readonly AppDatabase _database;
  private readonly SqliteAgentStore _store;
  private readonly SqliteUsageQueries _queries;

  public SqliteUsageQueriesWorkspaceShapeTests()
  {
    _database = new AppDatabase(_dbPath);
    _store = new SqliteAgentStore(_database);
    _queries = new SqliteUsageQueries(_database);
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

  /// <summary>One recorded call's token shape.</summary>
  private sealed record Call(int Input, int Output, int? Cached, string ModelId,
      string Provider = "vendor", string? WorkspaceId = Workspace);

  private async Task InsertAgentAsync(AgentId id, string? workspaceId, CancellationToken ct)
  {
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using SqliteCommand command = connection.CreateCommand();
#pragma warning restore CA2007
    command.CommandText = @"
        INSERT INTO agents (id, parent_id, depth, status, failure_reason, model_used, label,
            task_prompt, created_at, completed_at, final_report, workspace_id)
        VALUES (@id, NULL, 0, 1, NULL, 'test/model', NULL, 'prompt', @at, NULL, NULL, @ws);
        ";
    _ = command.Parameters.AddWithValue("@id", id.ToString());
    _ = command.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("o"));
    _ = command.Parameters.AddWithValue("@ws", workspaceId is null ? DBNull.Value : workspaceId);
    _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
  }

  /// <summary>Records one usage row through the production store.</summary>
  private async Task RecordAsync(AgentId id, Call call, CancellationToken ct)
  {
    Result<string> appended = await _store.AppendEventAsync(
        new AgentUsageRecorded(id, DateTimeOffset.UtcNow, Guid.NewGuid(), 0, call.Input,
            call.Output, call.Cached, null, UsageCallClass.MainLoop, call.WorkspaceId,
            call.Provider, 0.000001m, 0.0000001m, 0.000002m, call.ModelId,
            RequestedModelId: call.ModelId, ServedModelId: call.ModelId,
            DurationMs: 1000L, FirstTokenMs: 100L, ServedProvider: call.Provider), ct)
        .ConfigureAwait(false);
    Assert.True(appended.IsSuccess);
  }

  /// <summary>Writes many identical usage rows for one model in a single statement, so a test
  ///     can put more rows in the ledger than the performance read's row cap.</summary>
  private async Task BulkRecordAsync(AgentId id, string modelId, int rows, int inputTokens,
      int cachedTokens, CancellationToken ct)
  {
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using SqliteCommand command = connection.CreateCommand();
#pragma warning restore CA2007
    command.CommandText = @"
        WITH RECURSIVE counter(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM counter WHERE n < @rows)
        INSERT INTO agent_events (agent_id, occurred_at, type, payload_json)
        SELECT @id, @at, 'usage', json_object(
            'ModelId', @model, 'InputTokens', @input, 'CachedInputTokens', @cached,
            'OutputTokens', 1000, 'WorkspaceId', @ws, 'Provider', 'vendor')
        FROM counter;";
    _ = command.Parameters.AddWithValue("@rows", rows);
    _ = command.Parameters.AddWithValue("@id", id.ToString());
    _ = command.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("o"));
    _ = command.Parameters.AddWithValue("@model", modelId);
    _ = command.Parameters.AddWithValue("@input", inputTokens);
    _ = command.Parameters.AddWithValue("@cached", cachedTokens);
    _ = command.Parameters.AddWithValue("@ws", Workspace);
    _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
  }

  private async Task<CostShape?> ShapeAsync(CancellationToken ct)
  {
    Result<CostShape?> shape = await _queries.WorkspaceShapeAsync(ct).ConfigureAwait(false);
    Assert.True(shape.IsSuccess);
    return shape.Value;
  }

  [Fact]
  public async Task WorkspaceShape_IsTheMedianAcrossQualifyingModels()
  {
    // Three models, one hit rate each. The MEDIAN is the middle model's, which is what a
    // model that has never run here is priced at - not the token-weighted average, which one
    // model holding most of the tokens would decide.
    CancellationToken ct = TestContext.Current.CancellationToken;
    AgentId root = AgentId.NewId();
    await InsertAgentAsync(root, Workspace, ct).ConfigureAwait(true);
    // 90%, 80%, 70% hit rates, and output/input ratios of 0.010, 0.020, 0.030.
    await RecordAsync(root, new Call(1_000_000, 10_000, 900_000, "model/high"), ct).ConfigureAwait(true);
    await RecordAsync(root, new Call(1_000_000, 20_000, 800_000, "model/middle"), ct).ConfigureAwait(true);
    await RecordAsync(root, new Call(1_000_000, 30_000, 700_000, "model/low"), ct).ConfigureAwait(true);

    CostShape? shape = await ShapeAsync(ct).ConfigureAwait(true);

    CostShape resolved = Assert.NotNull(shape);
    Assert.Equal(0.8m, Math.Round(resolved.HitRate, 6));
    Assert.Equal(0.02m, Math.Round(resolved.OutputPerInputRatio, 6));
  }

  [Fact]
  public async Task WorkspaceShape_IgnoresModelsBelowTheEvidenceFloor()
  {
    // A model billed 999,999 tokens is real evidence but not enough of it: the floor is the
    // only threshold, and it is applied per model rather than per row.
    CancellationToken ct = TestContext.Current.CancellationToken;
    AgentId root = AgentId.NewId();
    await InsertAgentAsync(root, Workspace, ct).ConfigureAwait(true);
    await RecordAsync(root, new Call(1_000_000, 10_000, 900_000, "model/qualifying"), ct).ConfigureAwait(true);
    await RecordAsync(root, new Call(999_999, 10_000, 0, "model/short"), ct).ConfigureAwait(true);

    CostShape? shape = await ShapeAsync(ct).ConfigureAwait(true);

    CostShape resolved = Assert.NotNull(shape);
    Assert.Equal(0.9m, Math.Round(resolved.HitRate, 6));
  }

  [Fact]
  public async Task WorkspaceShape_IsNullWhenNoModelQualifies()
  {
    // Null is an ANSWER: a ledger holding no qualifying model has no workspace shape, and the
    // evidence chain falls through to its documented defaults.
    CancellationToken ct = TestContext.Current.CancellationToken;
    AgentId root = AgentId.NewId();
    await InsertAgentAsync(root, Workspace, ct).ConfigureAwait(true);
    await RecordAsync(root, new Call(100, 10, 90, "model/tiny"), ct).ConfigureAwait(true);

    CostShape? shape = await ShapeAsync(ct).ConfigureAwait(true);

    Assert.Null(shape);
  }

  [Fact]
  public async Task WorkspaceShape_ReadsTheWholeLedgerRatherThanTheNewestRows()
  {
    // THE DEFECT THIS EXISTS TO PREVENT: the performance read caps at 20,000 rows, and a
    // median over the newest rows is a different population from a median over the ledger.
    // The old model's single row is written FIRST, so a capped read cannot see it; the
    // uncapped median of the two models is 50%, the capped one would be 0%.
    CancellationToken ct = TestContext.Current.CancellationToken;
    AgentId root = AgentId.NewId();
    await InsertAgentAsync(root, Workspace, ct).ConfigureAwait(true);
    await RecordAsync(root, new Call(1_000_000, 10_000, 1_000_000, "model/old"), ct).ConfigureAwait(true);
    await BulkRecordAsync(root, "model/new", 20_000, 1_000_000, 0, ct).ConfigureAwait(true);

    CostShape? shape = await ShapeAsync(ct).ConfigureAwait(true);

    CostShape resolved = Assert.NotNull(shape);
    Assert.Equal(0.5m, Math.Round(resolved.HitRate, 6));
  }

  [Fact]
  public async Task WorkspaceShape_IsReadOnceAndReused()
  {
    // The aggregate costs hundreds of milliseconds on a large ledger, so it is read once per
    // instance. A later row that would move the median must not be picked up by a second call.
    CancellationToken ct = TestContext.Current.CancellationToken;
    AgentId root = AgentId.NewId();
    await InsertAgentAsync(root, Workspace, ct).ConfigureAwait(true);
    await RecordAsync(root, new Call(1_000_000, 10_000, 1_000_000, "model/first"), ct).ConfigureAwait(true);
    CostShape? first = await ShapeAsync(ct).ConfigureAwait(true);
    await RecordAsync(root, new Call(1_000_000, 10_000, 0, "model/second"), ct).ConfigureAwait(true);

    CostShape? second = await ShapeAsync(ct).ConfigureAwait(true);

    Assert.Equal(first, second);
    Assert.Equal(1m, Math.Round(Assert.NotNull(second).HitRate, 6));
  }

  [Fact]
  public async Task ProviderPerformance_ReportsEachPairsOutputTokens()
  {
    // The output/input ratio is derived from this count, so it must be the group's real sum
    // rather than a zero the record defaults to.
    CancellationToken ct = TestContext.Current.CancellationToken;
    AgentId root = AgentId.NewId();
    await InsertAgentAsync(root, Workspace, ct).ConfigureAwait(true);
    await RecordAsync(root, new Call(1000, 30, 900, "model/x"), ct).ConfigureAwait(true);
    await RecordAsync(root, new Call(1000, 70, 800, "model/x"), ct).ConfigureAwait(true);
    await RecordAsync(root, new Call(1000, 500, 0, "model/y"), ct).ConfigureAwait(true);

    Result<ProviderPerformanceReport> report = await _queries.ProviderPerformanceAsync(
        ProviderPerformanceRequest.ForSession(root.ToString(), Workspace), ct).ConfigureAwait(true);

    Assert.True(report.IsSuccess);
    Assert.Equal(100L, report.Value.Lookup("model/x", "vendor")!.OutputTokens);
    Assert.Equal(500L, report.Value.Lookup("model/y", "vendor")!.OutputTokens);
  }
}
