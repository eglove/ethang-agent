using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>The observed-performance read model (issue #295): per model/provider medians with
///     their sample counts (D1), outlier resistance (D2), the zero-output exclusion (D3), the
///     derived hit rate (D4), and the session -> workspace -> global fallback chain (D5).
///
///     Anti-tautology: rows are written through the PRODUCTION store
///     (<see cref="SqliteAgentStore.AppendEventAsync"/>) and read through the PRODUCTION read
///     model (<see cref="SqliteUsageQueries"/>), so a payload shape that drifted between the
///     write and read sides fails here rather than in a picker nobody re-read.</summary>
public sealed class SqliteUsageQueriesPerformanceTests : IDisposable
{
  private const string Workspace = "ws-perf";

  private readonly string _dbPath = Path.Combine(
      Path.GetTempPath(), $"ethang-usage-perf-{Guid.NewGuid():N}.db");
  private readonly AppDatabase _database;
  private readonly SqliteAgentStore _store;
  private readonly SqliteUsageQueries _queries;

  public SqliteUsageQueriesPerformanceTests()
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

  /// <summary>One recorded call's measured shape: what it produced and how long it took.
  ///     Grouped so the recording helper stays inside the repository's parameter budget.</summary>
  private sealed record Call(int Input, int Output, int? Cached, long? DurationMs,
      long? FirstTokenMs = null, string? Provider = null, string? ModelId = "test/model",
      string? WorkspaceId = Workspace);

  /// <summary>Inserts one agent row: the session scope walks parent links over the persisted
  ///     table, and the workspace scope attributes unstamped legacy rows through it.</summary>
  private async Task InsertAgentAsync(AgentId id, AgentId? parentId, string? workspaceId,
      CancellationToken ct)
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
        VALUES (@id, @parent, @depth, 1, NULL, 'test/model', NULL, 'prompt', @at, NULL, NULL, @ws);
        ";
    _ = command.Parameters.AddWithValue("@id", id.ToString());
    _ = command.Parameters.AddWithValue("@parent", parentId is { } parent ? parent.ToString() : DBNull.Value);
    _ = command.Parameters.AddWithValue("@depth", parentId is null ? 0 : 1);
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
            DurationMs: call.DurationMs, FirstTokenMs: call.FirstTokenMs,
            ServedProvider: call.Provider), ct).ConfigureAwait(false);
    Assert.True(appended.IsSuccess);
  }

  /// <summary>Writes one usage row verbatim, the way a build before issue #292 left it on
  ///     disk: no DurationMs, no FirstTokenMs, no ServedProvider member at all.</summary>
  private async Task InsertLegacyRowAsync(AgentId id, string payload, CancellationToken ct)
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
        INSERT INTO agent_events (agent_id, occurred_at, type, payload_json)
        VALUES (@id, @at, 'usage', @payload);
        ";
    _ = command.Parameters.AddWithValue("@id", id.ToString());
    _ = command.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("o"));
    _ = command.Parameters.AddWithValue("@payload", payload);
    _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
  }

  /// <summary>The report for one request, asserted successful: a failed read is a test
  ///     failure, never a silently empty report.</summary>
  private async Task<ProviderPerformanceReport> ReportAsync(ProviderPerformanceRequest request,
      CancellationToken ct)
  {
    Result<ProviderPerformanceReport> report = await _queries.ProviderPerformanceAsync(request, ct)
        .ConfigureAwait(false);
    Assert.True(report.IsSuccess);
    return report.Value;
  }

  [Fact]
  public async Task ProviderPerformance_ReportsMedianAndSampleCount()
  {
    // B1: the result carries the median duration AND the number of samples it rests on.
    CancellationToken ct = TestContext.Current.CancellationToken;
    AgentId root = AgentId.NewId();
    await InsertAgentAsync(root, null, Workspace, ct).ConfigureAwait(true);
    await RecordAsync(root, new Call(100, 10, null, 1000), ct).ConfigureAwait(true);
    await RecordAsync(root, new Call(100, 10, null, 2000), ct).ConfigureAwait(true);
    await RecordAsync(root, new Call(100, 10, null, 3000), ct).ConfigureAwait(true);

    ProviderPerformanceReport report = await ReportAsync(
        ProviderPerformanceRequest.ForSession(root.ToString(), Workspace), ct).ConfigureAwait(true);

    ProviderPerformance pair = Assert.Single(report.Providers);
    Assert.Equal(2000d, pair.MedianDurationMs);
    Assert.Equal(3, pair.LatencySamples);
    Assert.Equal(3, pair.Calls);
    Assert.Equal(0, pair.UnmeasuredCalls);
  }

  [Fact]
  public async Task ProviderPerformance_OneOutlierDoesNotMoveTheMedian()
  {
    // B2: the live case this rule exists for - a handful of typical calls and one that took
    // nearly ten minutes. The mean would be ~116 seconds; the median stays ~1 second.
    CancellationToken ct = TestContext.Current.CancellationToken;
    AgentId root = AgentId.NewId();
    await InsertAgentAsync(root, null, Workspace, ct).ConfigureAwait(true);
    await RecordAsync(root, new Call(100, 10, null, 900), ct).ConfigureAwait(true);
    await RecordAsync(root, new Call(100, 10, null, 1000), ct).ConfigureAwait(true);
    await RecordAsync(root, new Call(100, 10, null, 1100), ct).ConfigureAwait(true);
    await RecordAsync(root, new Call(100, 10, null, 578_000), ct).ConfigureAwait(true);

    ProviderPerformanceReport report = await ReportAsync(
        ProviderPerformanceRequest.ForSession(root.ToString()), ct).ConfigureAwait(true);

    ProviderPerformance pair = Assert.Single(report.Providers);
    Assert.Equal(1050d, pair.MedianDurationMs);
  }

  [Fact]
  public async Task ProviderPerformance_ZeroOutputCallIsExcludedFromThroughput()
  {
    // B3: a tool-call-only response produced no output, so it has no rate. Including it
    // would drag the throughput median toward zero.
    CancellationToken ct = TestContext.Current.CancellationToken;
    AgentId root = AgentId.NewId();
    await InsertAgentAsync(root, null, Workspace, ct).ConfigureAwait(true);
    // 1000 tokens in 1000 ms = 1000 tokens/second.
    await RecordAsync(root, new Call(100, 1000, null, 1000), ct).ConfigureAwait(true);
    // 3000 tokens in 1000 ms = 3000 tokens/second.
    await RecordAsync(root, new Call(100, 3000, null, 1000), ct).ConfigureAwait(true);
    // A tool-call-only response: no output at all.
    await RecordAsync(root, new Call(100, 0, null, 1000), ct).ConfigureAwait(true);

    ProviderPerformanceReport report = await ReportAsync(
        ProviderPerformanceRequest.ForSession(root.ToString()), ct).ConfigureAwait(true);

    ProviderPerformance pair = Assert.Single(report.Providers);
    Assert.Equal(2000d, pair.MedianOutputTokensPerSecond);
    Assert.Equal(2, pair.ThroughputSamples);
    // All three calls still count as calls: only the RATE excluded one.
    Assert.Equal(3, pair.Calls);
    Assert.Equal(3, pair.LatencySamples);
  }

  [Fact]
  public async Task ProviderPerformance_HitRateMatchesTheCountsItCameFrom()
  {
    // B4: the picker's projection and the status bar's rollup must be the same number.
    CancellationToken ct = TestContext.Current.CancellationToken;
    AgentId root = AgentId.NewId();
    await InsertAgentAsync(root, null, Workspace, ct).ConfigureAwait(true);
    await RecordAsync(root, new Call(1000, 10, 900, 1000), ct).ConfigureAwait(true);
    await RecordAsync(root, new Call(1000, 10, 800, 1000), ct).ConfigureAwait(true);

    ProviderPerformanceReport report = await ReportAsync(
        ProviderPerformanceRequest.ForSession(root.ToString()), ct).ConfigureAwait(true);

    // 1700 cached of 2000 input tokens.
    Assert.Equal(0.85m, report.HitRate);
    Assert.Equal(2000, report.InputTokens);
    Assert.Equal(1700, report.CachedInputTokens);
    Assert.Equal(report.HitRate, Assert.Single(report.Providers).HitRate);
  }

  [Fact]
  public async Task ProviderPerformance_SessionWithoutRows_FallsBackToWorkspaceAndNamesIt()
  {
    // B5: a fresh session has no data of its own, so the workspace's history answers - and
    // the report NAMES the scope that answered rather than implying it was the session's.
    CancellationToken ct = TestContext.Current.CancellationToken;
    AgentId oldSession = AgentId.NewId();
    AgentId freshSession = AgentId.NewId();
    await InsertAgentAsync(oldSession, null, Workspace, ct).ConfigureAwait(true);
    await InsertAgentAsync(freshSession, null, Workspace, ct).ConfigureAwait(true);
    await RecordAsync(oldSession, new Call(100, 10, null, 1500), ct).ConfigureAwait(true);

    ProviderPerformanceReport report = await ReportAsync(
        ProviderPerformanceRequest.ForSession(freshSession.ToString(), Workspace), ct).ConfigureAwait(true);

    Assert.Equal(UsageScope.Workspace, report.Scope);
    Assert.Equal(1500d, Assert.Single(report.Providers).MedianDurationMs);
  }

  [Fact]
  public async Task ProviderPerformance_SessionWithRows_AnswersFromTheSessionScope()
  {
    // The precedence is real: a session WITH history is never answered from a wider scope,
    // even when the workspace holds rows the session did not produce.
    CancellationToken ct = TestContext.Current.CancellationToken;
    AgentId mine = AgentId.NewId();
    AgentId other = AgentId.NewId();
    await InsertAgentAsync(mine, null, Workspace, ct).ConfigureAwait(true);
    await InsertAgentAsync(other, null, Workspace, ct).ConfigureAwait(true);
    await RecordAsync(mine, new Call(100, 10, null, 1500), ct).ConfigureAwait(true);
    await RecordAsync(other, new Call(100, 10, null, 90_000), ct).ConfigureAwait(true);

    ProviderPerformanceReport report = await ReportAsync(
        ProviderPerformanceRequest.ForSession(mine.ToString(), Workspace), ct).ConfigureAwait(true);

    Assert.Equal(UsageScope.Session, report.Scope);
    Assert.Equal(1, report.Calls);
    Assert.Equal(1500d, Assert.Single(report.Providers).MedianDurationMs);
  }

  [Fact]
  public async Task ProviderPerformance_NoRowsAnywhere_IsAnEmptyReportNotAFailure()
  {
    // A first-ever session has measured nothing. That is a STATE the surface renders, not an
    // error it reports.
    CancellationToken ct = TestContext.Current.CancellationToken;
    AgentId root = AgentId.NewId();
    await InsertAgentAsync(root, null, "ws-empty", ct).ConfigureAwait(true);

    ProviderPerformanceReport report = await ReportAsync(
        ProviderPerformanceRequest.ForSession(root.ToString(), "ws-empty"), ct).ConfigureAwait(true);

    Assert.False(report.HasRows);
    Assert.Null(report.HitRate);
    Assert.Empty(report.Providers);
  }

  [Fact]
  public async Task ProviderPerformance_LegacyRowWithNoDuration_IsExcludedAndCounted()
  {
    // B6: a row written before issue #292 carries no DurationMs at all. It must be EXCLUDED
    // from the latency median and COUNTED as unmeasured - never treated as a zero-duration
    // call, which would report the provider as instant.
    CancellationToken ct = TestContext.Current.CancellationToken;
    AgentId root = AgentId.NewId();
    await InsertAgentAsync(root, null, Workspace, ct).ConfigureAwait(true);
    await RecordAsync(root, new Call(100, 10, null, 2000), ct).ConfigureAwait(true);
    string legacyPayload = "{\"AgentId\":{\"Value\":\"" + root.ToString()
        + "\"},\"TurnId\":\"11111111-2222-3333-4444-555555555555\",\"Iteration\":0,"
        + "\"InputTokens\":100,\"OutputTokens\":10,\"ModelId\":\"test/model\","
        + "\"Provider\":\"legacy-vendor\"}";
    await InsertLegacyRowAsync(root, legacyPayload, ct).ConfigureAwait(true);

    ProviderPerformanceReport report = await ReportAsync(
        ProviderPerformanceRequest.ForSession(root.ToString()), ct).ConfigureAwait(true);

    // Two calls, one measured: the legacy row is counted, not silently dropped.
    Assert.Equal(2, report.Calls);
    Assert.Equal(1, report.UnmeasuredCalls);
    ProviderPerformance measured = Assert.Single(report.Providers,
        p => p.ProviderName == "legacy-vendor");
    Assert.Null(measured.MedianDurationMs);
    Assert.Equal(0, measured.LatencySamples);
    Assert.Equal(1, measured.UnmeasuredCalls);
    Assert.Equal(1, measured.Calls);
  }

  [Fact]
  public async Task ProviderPerformance_GroupsByModelAndProviderPair()
  {
    // Latency is a property of the PAIR: the same vendor serving two models produces two
    // different numbers, and averaging them would describe neither.
    CancellationToken ct = TestContext.Current.CancellationToken;
    AgentId root = AgentId.NewId();
    await InsertAgentAsync(root, null, Workspace, ct).ConfigureAwait(true);
    await RecordAsync(root, new Call(100, 10, null, 1000, Provider: "vendor-a", ModelId: "model/x"), ct)
        .ConfigureAwait(true);
    await RecordAsync(root, new Call(100, 10, null, 5000, Provider: "vendor-a", ModelId: "model/y"), ct)
        .ConfigureAwait(true);

    ProviderPerformanceReport report = await ReportAsync(
        ProviderPerformanceRequest.ForSession(root.ToString()), ct).ConfigureAwait(true);

    Assert.Equal(2, report.Providers.Count);
    Assert.Equal(1000d, report.Lookup("model/x", "vendor-a")!.MedianDurationMs);
    Assert.Equal(5000d, report.Lookup("model/y", "vendor-a")!.MedianDurationMs);
  }

  [Fact]
  public async Task ProviderPerformance_ReportsTheSlowTailRatio()
  {
    // The reliability signal: how much slower the p90 is than the median, which a median
    // alone hides. Both ends are measurements, so the ratio invents no threshold.
    CancellationToken ct = TestContext.Current.CancellationToken;
    AgentId root = AgentId.NewId();
    await InsertAgentAsync(root, null, Workspace, ct).ConfigureAwait(true);
    // 20 samples, the slowest 4 at 8000 ms: nearest-rank p90 lands on a slow call, so the
    // ratio is a real measurement of the tail rather than an interpolation between two.
    for (int i = 0; i < 16; i++)
    {
      await RecordAsync(root, new Call(100, 10, null, 1000), ct).ConfigureAwait(true);
    }

    for (int i = 0; i < 4; i++)
    {
      await RecordAsync(root, new Call(100, 10, null, 8000), ct).ConfigureAwait(true);
    }

    ProviderPerformanceReport report = await ReportAsync(
        ProviderPerformanceRequest.ForSession(root.ToString()), ct).ConfigureAwait(true);

    ProviderPerformance pair = Assert.Single(report.Providers);
    Assert.Equal(1000d, pair.MedianDurationMs);
    Assert.Equal(8000d, pair.P90DurationMs);
    Assert.Equal(8d, pair.SlowTailRatio);
  }

  [Fact]
  public async Task ProviderPerformance_NoIdentifiersAtAll_IsANamedFailure()
  {
    // A query with nothing to scope by is a caller bug, not an empty answer: answering from
    // the whole ledger would present every workspace's history as the caller's.
    Result<ProviderPerformanceReport> report = await _queries.ProviderPerformanceAsync(
        new ProviderPerformanceRequest(), TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(report.IsSuccess);
  }

  [Fact]
  public async Task ProviderPerformance_ReadsThroughTheReadOnlyConnectionOnly()
  {
    // The read path must never mutate the ledger it measures: SQLite refuses writes on a
    // read-only connection at the engine level.
    CancellationToken ct = TestContext.Current.CancellationToken;
    AgentId root = AgentId.NewId();
    await InsertAgentAsync(root, null, Workspace, ct).ConfigureAwait(true);
    await RecordAsync(root, new Call(100, 10, null, 1000), ct).ConfigureAwait(true);

    ProviderPerformanceReport report = await ReportAsync(
        ProviderPerformanceRequest.ForSession(root.ToString()), ct).ConfigureAwait(true);
    Assert.Equal(1, report.Calls);

    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using SqliteConnection readonlyConnection = _database.OpenReadOnly();
#pragma warning restore CA2007
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using SqliteCommand write = readonlyConnection.CreateCommand();
#pragma warning restore CA2007
    write.CommandText = "DELETE FROM agent_events;";
    _ = await Assert.ThrowsAsync<SqliteException>(() => write.ExecuteNonQueryAsync(ct));
  }
}
