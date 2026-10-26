using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>The usage ledger's READ model (issue #24): rollups over the append-only
///     <c>agent_events</c> rows, with session attribution by WALKING
///     <c>agents.parent_id</c> from the root (B11), null prices contributing tokens but no
///     cost (B12), the legacy null class reading as <c>main_loop</c>, and reads running on
///     the read-only connection.
///
///     Anti-tautology: rows are written through the PRODUCTION store
///     (<see cref="SqliteAgentStore.AppendEventAsync"/>) and read through the PRODUCTION
///     read model (<see cref="SqliteUsageQueries"/>), so a payload shape that drifted
///     between the write and read sides fails here rather than in a live session.</summary>
public sealed class SqliteUsageQueriesTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(
      Path.GetTempPath(), $"ethang-usage-read-{Guid.NewGuid():N}.db");
  private readonly AppDatabase _database;
  private readonly SqliteAgentStore _store;
  private readonly SqliteUsageQueries _queries;

  public SqliteUsageQueriesTests()
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

  /// <summary>One recorded call's price basis, grouped so the recording helper stays
  ///     inside the repository's parameter budget.</summary>
  private sealed record Prices(decimal? Prompt, decimal? CacheRead, decimal? Completion)
  {
    public static Prices None { get; } = new(null, null, null);

    public static Prices Known { get; } = new(0.000001m, 0.0000001m, 0.000002m);
  }

  /// <summary>Inserts one agent row directly: the read model walks parent links, and the
  ///     walk is over the persisted table, not over anything the event log carries.</summary>
  private async Task InsertAgentAsync(AgentId id, AgentId? parentId, CancellationToken ct)
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
            task_prompt, created_at, completed_at, final_report)
        VALUES (@id, @parent, @depth, 1, NULL, 'test/model', NULL, 'prompt', @at, NULL, NULL);
        ";
    _ = command.Parameters.AddWithValue("@id", id.ToString());
    _ = command.Parameters.AddWithValue("@parent", parentId is { } parent ? parent.ToString() : DBNull.Value);
    _ = command.Parameters.AddWithValue("@depth", parentId is null ? 0 : 1);
    _ = command.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("o"));
    _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
  }

  /// <summary>One recorded call's token counts, grouped so the recording helper stays inside
  ///     the repository's parameter budget.</summary>
  private sealed record Tokens(int Input, int Output, int? Cached);

  /// <summary>Records one usage row through the production store.</summary>
  private async Task RecordAsync(AgentId id, Tokens tokens, UsageCallClass? callClass,
      string? modelId, Prices prices, CancellationToken ct)
  {
    Result<string> appended = await _store.AppendEventAsync(
        new AgentUsageRecorded(id, DateTimeOffset.UtcNow, Guid.NewGuid(), 0, tokens.Input,
            tokens.Output, tokens.Cached, null, callClass, "ws-1", "openrouter", prices.Prompt,
            prices.CacheRead, prices.Completion, modelId), ct).ConfigureAwait(false);
    Assert.True(appended.IsSuccess);
  }

  /// <summary>Records one usage row that names BOTH the requested and the served model
  ///     (issue #290), so the read model's grouping key is pinned against a router row.</summary>
  private async Task RecordWithServedAsync(AgentId id, Tokens tokens, string requestedModelId,
      string servedModelId, CancellationToken ct)
  {
    Result<string> appended = await _store.AppendEventAsync(
        new AgentUsageRecorded(id, DateTimeOffset.UtcNow, Guid.NewGuid(), 0, tokens.Input,
            tokens.Output, tokens.Cached, null, UsageCallClass.MainLoop, "ws-1", "openrouter",
            Prices.Known.Prompt, Prices.Known.CacheRead, Prices.Known.Completion,
            ModelId: servedModelId, RequestedModelId: requestedModelId,
            ServedModelId: servedModelId), ct).ConfigureAwait(false);
    Assert.True(appended.IsSuccess);
  }

  /// <summary>Writes one usage row verbatim, the way a pre-#22 build left it on disk.</summary>
  private async Task InsertRawUsageRowAsync(AgentId id, string payload, CancellationToken ct)
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

  /// <summary>The session summary for one root, asserted successful: a failed read is a
  ///     test failure, never a silently empty summary.</summary>
  private async Task<UsageSummary> SummarizeAsync(AgentId root, CancellationToken ct)
  {
    Result<UsageSummary> summary = await _queries.SessionSummaryAsync(root.ToString(), ct)
        .ConfigureAwait(false);
    Assert.True(summary.IsSuccess);
    return summary.Value;
  }

  [Fact]
  public async Task SessionSummary_UnknownSession_IsAnEmptySummary()
  {
    // A session that has not run a turn yet has no rows. That is a STATE, not an error: the
    // status bar must render it rather than surfacing a failure.
    Result<UsageSummary> summary = await _queries.SessionSummaryAsync(
        AgentId.NewId().ToString(), TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(summary.IsSuccess);
    Assert.False(summary.Value.HasRows);
    Assert.Null(summary.Value.Totals.Cost);
    Assert.Empty(summary.Value.ByClass);
    Assert.Empty(summary.Value.ByModel);
  }

  [Fact]
  public async Task SessionSummary_IncludesEveryDescendantAgent_ByWalkingParentLinks()
  {
    // B11, the reason the walk exists: a child stamps its OWN id as its session id, so a
    // session_id = root filter would omit every subagent's spend.
    CancellationToken ct = TestContext.Current.CancellationToken;
    AgentId root = AgentId.NewId();
    AgentId child = AgentId.NewId();
    AgentId grandchild = AgentId.NewId();
    AgentId otherSession = AgentId.NewId();
    await InsertAgentAsync(root, null, ct).ConfigureAwait(true);
    await InsertAgentAsync(child, root, ct).ConfigureAwait(true);
    await InsertAgentAsync(grandchild, child, ct).ConfigureAwait(true);
    await InsertAgentAsync(otherSession, null, ct).ConfigureAwait(true);

    await RecordAsync(root, new Tokens(1000, 100, 800), UsageCallClass.MainLoop, "test/model", Prices.Known, ct)
        .ConfigureAwait(true);
    await RecordAsync(child, new Tokens(500, 50, 400), UsageCallClass.MainLoop, "test/model", Prices.Known, ct)
        .ConfigureAwait(true);
    await RecordAsync(grandchild, new Tokens(250, 25, 200), UsageCallClass.Compaction, "test/model", Prices.Known, ct)
        .ConfigureAwait(true);
    await RecordAsync(otherSession, new Tokens(9999, 999, 0), UsageCallClass.MainLoop, "test/model", Prices.Known, ct)
        .ConfigureAwait(true);

    UsageSummary summary = await SummarizeAsync(root, ct).ConfigureAwait(true);

    Assert.Equal(3, summary.Totals.Calls);
    Assert.Equal(1750, summary.Totals.InputTokens);
    Assert.Equal(175, summary.Totals.OutputTokens);
    Assert.Equal(1400, summary.Totals.CachedInputTokens);
    // The other session's rows are NOT in this session's spend.
    Assert.DoesNotContain(summary.ByClass, c => c.Totals.InputTokens == 9999);
  }

  [Fact]
  public async Task SessionSummary_ChargesCachedTokensAtTheCacheReadPrice()
  {
    // The revised formula (parent decision 5): uncached x prompt + cached x cache-read
    // + output x completion. Charging every input token at the prompt price overstated
    // real spend ~8x at this workspace's cache-hit rate.
    CancellationToken ct = TestContext.Current.CancellationToken;
    AgentId root = AgentId.NewId();
    await InsertAgentAsync(root, null, ct).ConfigureAwait(true);
    await RecordAsync(root, new Tokens(1000, 100, 900), UsageCallClass.MainLoop, "test/model",
        new Prices(0.000002m, 0.0000002m, 0.000004m), ct).ConfigureAwait(true);

    UsageSummary summary = await SummarizeAsync(root, ct).ConfigureAwait(true);

    // 100 uncached x 0.000002 = 0.0002; 900 cached x 0.0000002 = 0.00018;
    // 100 output x 0.000004 = 0.0004. Total 0.00078.
    Assert.Equal(0.00078m, summary.Totals.Cost);
    Assert.Equal(1, summary.Totals.PricedCalls);
    Assert.Equal(0, summary.Totals.UnpricedCalls);
  }

  [Fact]
  public async Task SessionSummary_NullPricesContributeTokensButNoCost()
  {
    // B12: a row whose prices are unknown contributes tokens and NO cost. The summary
    // reports both, so a partial cost is never presented as the whole bill.
    CancellationToken ct = TestContext.Current.CancellationToken;
    AgentId root = AgentId.NewId();
    await InsertAgentAsync(root, null, ct).ConfigureAwait(true);
    await RecordAsync(root, new Tokens(1000, 100, 900), UsageCallClass.MainLoop, "priced/model",
        new Prices(0.000002m, 0.0000002m, 0.000004m), ct).ConfigureAwait(true);
    await RecordAsync(root, new Tokens(4000, 400, null), UsageCallClass.MainLoop, "unpriced/model",
        Prices.None, ct).ConfigureAwait(true);

    UsageSummary summary = await SummarizeAsync(root, ct).ConfigureAwait(true);

    Assert.Equal(5000, summary.Totals.InputTokens);
    Assert.Equal(500, summary.Totals.OutputTokens);
    Assert.Equal(0.00078m, summary.Totals.Cost);
    Assert.Equal(1, summary.Totals.PricedCalls);
    Assert.Equal(1, summary.Totals.UnpricedCalls);
    Assert.Equal(2, summary.Totals.Calls);
  }

  [Fact]
  public async Task SessionSummary_NoPricedRowAtAll_ReportsNullCost()
  {
    // A session whose every row is unpriced must read as 'cost unknown', never as free.
    CancellationToken ct = TestContext.Current.CancellationToken;
    AgentId root = AgentId.NewId();
    await InsertAgentAsync(root, null, ct).ConfigureAwait(true);
    await RecordAsync(root, new Tokens(100, 10, null), UsageCallClass.MainLoop, "free/model", Prices.None, ct)
        .ConfigureAwait(true);

    UsageSummary summary = await SummarizeAsync(root, ct).ConfigureAwait(true);

    Assert.Null(summary.Totals.Cost);
    Assert.False(summary.HasPricedSpend);
    Assert.True(summary.HasRows);
  }

  [Fact]
  public async Task SessionSummary_GroupsByCallClassAndByModel()
  {
    CancellationToken ct = TestContext.Current.CancellationToken;
    AgentId root = AgentId.NewId();
    await InsertAgentAsync(root, null, ct).ConfigureAwait(true);
    await RecordAsync(root, new Tokens(100, 10, null), UsageCallClass.MainLoop, "model/a",
        new Prices(0.000001m, null, 0.000002m), ct).ConfigureAwait(true);
    await RecordAsync(root, new Tokens(200, 20, null), UsageCallClass.MainLoop, "model/a",
        new Prices(0.000001m, null, 0.000002m), ct).ConfigureAwait(true);
    await RecordAsync(root, new Tokens(300, 30, null), UsageCallClass.Compaction, "model/b",
        new Prices(0.000003m, null, 0.000006m), ct).ConfigureAwait(true);

    UsageSummary summary = await SummarizeAsync(root, ct).ConfigureAwait(true);

    UsageClassTotal mainLoop = Assert.Single(summary.ByClass, c => c.CallClass == "main_loop");
    Assert.Equal(2, mainLoop.Totals.Calls);
    Assert.Equal(300, mainLoop.Totals.InputTokens);
    UsageClassTotal compaction = Assert.Single(summary.ByClass, c => c.CallClass == "compaction");
    Assert.Equal(1, compaction.Totals.Calls);
    Assert.Equal(300, compaction.Totals.InputTokens);

    UsageModelTotal modelA = Assert.Single(summary.ByModel, m => m.ModelId == "model/a");
    Assert.Equal(2, modelA.Totals.Calls);
    UsageModelTotal modelB = Assert.Single(summary.ByModel, m => m.ModelId == "model/b");
    Assert.Equal(1, modelB.Totals.Calls);
  }

  [Fact]
  public async Task SessionSummary_RouterRows_GroupOnTheServedModel()
  {
    // Issue #290: the per-model rollup groups on what was BILLED. A router session that
    // requested openrouter/free but was served by two different models must produce two
    // model buckets - and no bucket for the alias, which ran nothing.
    CancellationToken ct = TestContext.Current.CancellationToken;
    AgentId root = AgentId.NewId();
    await InsertAgentAsync(root, null, ct).ConfigureAwait(true);
    await RecordWithServedAsync(root, new Tokens(100, 10, null), "openrouter/free",
        "cohere/north-mini-code:free", ct).ConfigureAwait(true);
    await RecordWithServedAsync(root, new Tokens(200, 20, null), "openrouter/free",
        "google/gemini-flash:free", ct).ConfigureAwait(true);

    UsageSummary summary = await SummarizeAsync(root, ct).ConfigureAwait(true);

    Assert.Equal(2, summary.Totals.Calls);
    UsageModelTotal served = Assert.Single(summary.ByModel, m => m.ModelId == "cohere/north-mini-code:free");
    Assert.Equal(1, served.Totals.Calls);
    Assert.Equal(100, served.Totals.InputTokens);
    _ = Assert.Single(summary.ByModel, m => m.ModelId == "google/gemini-flash:free");
    Assert.DoesNotContain(summary.ByModel, m => m.ModelId == "openrouter/free");
  }

  [Fact]
  public async Task SessionSummary_LegacyRowWithNoClassCountsAsMainLoop()
  {
    // B-legacy: a row written before the class existed carries no CallClass member at all.
    // It must count as main_loop, because every row written before the class existed came
    // from the loop.
    CancellationToken ct = TestContext.Current.CancellationToken;
    AgentId root = AgentId.NewId();
    await InsertAgentAsync(root, null, ct).ConfigureAwait(true);
    string legacyPayload = "{\"AgentId\":{\"Value\":\"" + root.ToString()
        + "\"},\"TurnId\":\"11111111-2222-3333-4444-555555555555\",\"Iteration\":0,"
        + "\"InputTokens\":100,\"OutputTokens\":10}";
    await InsertRawUsageRowAsync(root, legacyPayload, ct).ConfigureAwait(true);

    UsageSummary summary = await SummarizeAsync(root, ct).ConfigureAwait(true);

    Assert.Equal(1, summary.Totals.Calls);
    UsageClassTotal mainLoop = Assert.Single(summary.ByClass);
    Assert.Equal("main_loop", mainLoop.CallClass);
    // No prices on a legacy row: tokens counted, cost unknown.
    Assert.Null(summary.Totals.Cost);
    Assert.Equal(1, summary.Totals.UnpricedCalls);
  }

  [Fact]
  public async Task SessionSummary_ReadsThroughTheReadOnlyConnectionOnly()
  {
    // The read path must never mutate the ledger it measures. SQLite refuses writes on a
    // read-only connection at the engine level, so a rollup that somehow tried one would
    // fail here rather than in production.
    CancellationToken ct = TestContext.Current.CancellationToken;
    AgentId root = AgentId.NewId();
    await InsertAgentAsync(root, null, ct).ConfigureAwait(true);
    await RecordAsync(root, new Tokens(10, 1, null), UsageCallClass.MainLoop, "m",
        new Prices(0.1m, null, 0.2m), ct).ConfigureAwait(true);

    UsageSummary summary = await SummarizeAsync(root, ct).ConfigureAwait(true);
    Assert.Equal(1, summary.Totals.Calls);

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

  [Fact]
  public async Task SessionSummary_AgentSelfInspectionCanReadTheRows()
  {
    // B15: the agent's own db_query already reads agent_events on the read-only
    // connection. Pinned rather than built - this is the acceptance criterion 'computable
    // from persisted data alone'.
    CancellationToken ct = TestContext.Current.CancellationToken;
    AgentId root = AgentId.NewId();
    await InsertAgentAsync(root, null, ct).ConfigureAwait(true);
    await RecordAsync(root, new Tokens(1234, 56, 1000), UsageCallClass.MainLoop, "test/model", Prices.Known, ct)
        .ConfigureAwait(true);
    SqliteSelfDatabaseAccess self = new(_database);

    Result<SelfQueryResult> read = await self.QueryAsync(
        "SELECT payload_json FROM agent_events WHERE type='usage'", 10, ct).ConfigureAwait(true);

    Assert.True(read.IsSuccess);
    _ = Assert.Single(read.Value.Rows);
  }
}
