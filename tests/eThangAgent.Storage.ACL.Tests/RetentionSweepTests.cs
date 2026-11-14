using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>The app-level retention sweep (issue #246): rows older than a configured window, from
///     a named set of tables, followed by a reclaim that returns the space to the disk.
///     <para>WHAT IS ASSERTED IS THE OUTCOME the issue sized its criteria against: the rows are
///     gone, the tables NOT in scope are untouched, and <c>freelist_count</c> is zero afterwards -
///     a plain DELETE leaves the pages in the file, measured on the real database as a strip that
///     moved it by exactly zero bytes until the VACUUM ran.</para>
///     <para>THE CHAINS ARE THE ONE EXCLUSION WORTH ITS OWN TEST: a chain is the record of which
///     sessions ran and what they cost, and when a workspace directory is gone it is the only
///     surviving evidence of that work. It is deleted only by explicit user action.</para></summary>
public sealed class RetentionSweepTests : IDisposable
{
  private readonly string _dbPath = TemplateDatabase.Copy();
  private readonly FixedClock _clock = new(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
  private readonly AppDatabase _database;
  private readonly RetentionSweep _sweep;

  public RetentionSweepTests()
  {
    _database = new AppDatabase(_dbPath);
    _sweep = new RetentionSweep(_database, new MemoryPreferences(), _clock);
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

  /// <summary>Rows older than the window go and newer rows stay, in the same pass.</summary>
  [Fact]
  public async Task RowsOlderThanTheWindow_Go_AndNewerOnesStay()
  {
    await SeedAgentAsync("old", _clock.GetUtcNow().AddDays(-40)).ConfigureAwait(true);
    await SeedAgentAsync("new", _clock.GetUtcNow().AddDays(-1)).ConfigureAwait(true);

    Result<RetentionRun> run = await _sweep.RunAsync(TimeSpan.FromDays(30),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(run.IsSuccess, run.Error?.Message);
    Assert.Equal(1, run.Value.Deleted["agents"]);
    Assert.Equal(1, run.Value.Deleted["agent_messages"]);
    Assert.Equal(["new"], await AgentIdsAsync().ConfigureAwait(true));
  }

  /// <summary>No transcript row outlives the agent row it belongs to, and no agent row is pruned
  ///     while its messages remain: the order inside the one transaction is the only thing that
  ///     enforces it, because agent_messages carries no foreign key.</summary>
  [Fact]
  public async Task TheCascade_LeavesNoOrphanedMessageRows()
  {
    await SeedAgentAsync("old", _clock.GetUtcNow().AddDays(-40)).ConfigureAwait(true);

    _ = await _sweep.RunAsync(TimeSpan.FromDays(30), TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.Equal(0L, await ScalarAsync(
        "SELECT COUNT(*) FROM agent_messages WHERE agent_id NOT IN (SELECT id FROM agents);")
        .ConfigureAwait(true));
  }

  /// <summary>The reclaim moves the file and leaves no free pages - the criterion the issue
  ///     insists on, because a DELETE alone does not change the file's size at all.</summary>
  [Fact]
  public async Task TheReclaim_ShrinksTheFile_AndLeavesNoFreePages()
  {
    for (int i = 0; i < 40; i++)
    {
      await SeedAgentAsync("old-" + i, _clock.GetUtcNow().AddDays(-40), bodySize: 8 * 1024)
          .ConfigureAwait(true);
    }

    Result<RetentionRun> run = await _sweep.RunAsync(TimeSpan.FromDays(30),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(run.IsSuccess, run.Error?.Message);
    Assert.Null(run.Value.ReclaimFailure);
    Assert.Equal(0L, run.Value.FreePagesAfter);
    Assert.True(run.Value.BytesReclaimed > 0,
        $"the file must shrink: {run.Value.FileBytesBefore} bytes before, {run.Value.FileBytesAfter} after");
  }

  /// <summary>The tables the issue excluded stay: a chain and its steps survive a sweep that
  ///     would have taken every other old row on the same database.</summary>
  [Fact]
  public async Task Chains_AreNeverSwept()
  {
    await InsertChainAsync().ConfigureAwait(true);

    _ = await _sweep.RunAsync(TimeSpan.FromDays(1), TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM chains;").ConfigureAwait(true));
    Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM chain_steps;").ConfigureAwait(true));
  }

  /// <summary>A row older than the window goes from a table this build's migration table no
  ///     longer creates, and one inside it stays. The sweep names the tables it touches, so a
  ///     table a database from an older build lost is skipped rather than failing the run.
  ///     </summary>
  [Fact]
  public async Task ASweptTable_DeletesByItsOwnTimestamp_AndASkippedOneDoesNotFailTheRun()
  {
    await ExecuteAsync("INSERT INTO state_keys (workspace_id, ns, name, value, version, updated_at) "
        + "VALUES ('w', 'ns', 'old', 'v', 1, $at);", _clock.GetUtcNow().AddDays(-40)).ConfigureAwait(true);
    await ExecuteAsync("INSERT INTO state_keys (workspace_id, ns, name, value, version, updated_at) "
        + "VALUES ('w', 'ns', 'new', 'v', 1, $at);", _clock.GetUtcNow().AddDays(-1)).ConfigureAwait(true);

    Result<RetentionRun> run = await _sweep.RunAsync(TimeSpan.FromDays(30),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(run.IsSuccess, run.Error?.Message);
    Assert.Equal(1, run.Value.Deleted["state_keys"]);
    Assert.Equal(0, run.Value.Deleted["tool_output_archive"]);
    Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM state_keys;").ConfigureAwait(true));
  }

  /// <summary>A stored window of '0' means the user disabled retention, and a run deletes
  ///     nothing rather than treating it as 'everything is old'.</summary>
  [Fact]
  public async Task ADisabledWindow_DeletesNothing()
  {
    MemoryPreferences preferences = new();
    _ = await preferences.SetAsync(RetentionSweep.WindowHoursKey, "0",
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    RetentionSweep sweep = new(_database, preferences, _clock);
    await SeedAgentAsync("old", _clock.GetUtcNow().AddDays(-400)).ConfigureAwait(true);

    Result<RetentionRun?> run = await sweep.RunOnceAsync(TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.True(run.IsSuccess, run.Error?.Message);
    Assert.Null(run.Value);
    Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM agents;").ConfigureAwait(true));
  }

  /// <summary>An unset window is the thirty-day default, which the port reports as a window
  ///     rather than as 'disabled'.</summary>
  [Fact]
  public async Task AnUnsetWindow_IsTheThirtyDayDefault()
  {
    TimeSpan? window = await _sweep.ConfiguredWindowAsync(TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.Equal(RetentionSweep.DefaultWindow, window);
  }

  private async Task SeedAgentAsync(string label, DateTimeOffset createdAt, int bodySize = 256)
  {
    string id = Guid.NewGuid().ToString();
    await ExecuteAsync(
        "INSERT INTO agents (id, status, failure_reason, model_used, label, task_prompt, created_at, "
        + "completed_at, final_report, workspace_id, provider, attempts, phase, contract) "
        + "VALUES ($id, 2, NULL, 'provider/model', $label, 'task', $at, NULL, NULL, NULL, NULL, 0, NULL, NULL);",
        createdAt, ("$id", id), ("$label", label)).ConfigureAwait(true);
    await ExecuteAsync(
        "INSERT INTO agent_messages (agent_id, seq, role, content, meta_json) "
        + "VALUES ($id, 0, 'Assistant', $body, '{\"Timestamp\":\"2026-01-01T00:00:00+00:00\",\"ToolCalls\":null,\"ToolCallId\":null}');",
        createdAt, ("$id", id), ("$body", new string('x', bodySize))).ConfigureAwait(true);
  }

  private async Task InsertChainAsync()
  {
    await ExecuteAsync(
        "INSERT INTO chains (id, workspace_id, plan_id, plan_version, title, status, created_at, "
        + "updated_at, version) VALUES (1, 'w', 1, 1, 'old chain', 'Completed', $at, $at, 1);",
        _clock.GetUtcNow().AddDays(-400)).ConfigureAwait(true);
    await ExecuteAsync(
        "INSERT INTO chain_steps (chain_id, position, status, session_id, attempts, outcome, actor) "
        + "VALUES (1, 1, 'Done', 's', 1, 'done', 'Implement');").ConfigureAwait(true);
  }

  private async Task<IReadOnlyList<string>> AgentIdsAsync()
  {
#pragma warning disable CA2007
    await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT label FROM agents ORDER BY label;";
    using SqliteDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    List<string> labels = [];
    while (await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
    {
      labels.Add(reader.GetString(0));
    }

    return labels;
  }

  private async Task<long> ScalarAsync(string sql)
  {
#pragma warning disable CA2007
    await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
    using SqliteCommand command = connection.CreateCommand();
    // Named decision (CA2100): the caller passes a literal from this test file.
#pragma warning disable CA2100 // Review SQL query for security vulnerabilities
    command.CommandText = sql;
#pragma warning restore CA2100
    return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)
        .ConfigureAwait(true), System.Globalization.CultureInfo.InvariantCulture);
  }

  private async Task ExecuteAsync(string sql, params (string Name, string Value)[] parameters)
      => await ExecuteAsync(sql, _clock.GetUtcNow(), parameters).ConfigureAwait(true);

  private async Task ExecuteAsync(string sql, DateTimeOffset at,
      params (string Name, string Value)[] parameters)
  {
#pragma warning disable CA2007
    await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
    using SqliteCommand command = connection.CreateCommand();
    // Named decision (CA2100): the caller passes a literal from this test file.
#pragma warning disable CA2100 // Review SQL query for security vulnerabilities
    command.CommandText = sql;
#pragma warning restore CA2100
    _ = command.Parameters.AddWithValue("$at", at.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
    foreach ((string name, string value) in parameters)
    {
      _ = command.Parameters.AddWithValue(name, value);
    }

    _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
  }

  /// <summary>A fixed clock, so a window is a fact about the fixture rather than about today.
  ///     </summary>
  private sealed class FixedClock(DateTimeOffset now) : TimeProvider
  {
    public override DateTimeOffset GetUtcNow() => now;
  }

  /// <summary>An in-memory preference store, so a window can be set without a database.</summary>
  private sealed class MemoryPreferences : IAppPreferenceStore
  {
    private readonly Dictionary<string, string> _values = [];

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => Task.FromResult(_values.TryGetValue(key, out string? value) ? value : null);

    public Task<bool> SetAsync(string key, string value, CancellationToken ct = default)
    {
      _values[key] = value;
      return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(string key, CancellationToken ct = default)
      => Task.FromResult(_values.Remove(key));
  }
}
