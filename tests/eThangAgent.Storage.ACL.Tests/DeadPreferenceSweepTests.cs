using System.Globalization;
using eThangAgent.SharedKernel;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Issue #298: the startup sweep removes preference rows whose feature was
///     deleted and nothing else. B2 pins that the named dead keys go; B3 pins that a key
///     outside the named prefixes survives - the whole reason the prefixes are named
///     explicitly rather than matched as a pattern.</summary>
public sealed class DeadPreferenceSweepTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ethang-sweep-{Guid.NewGuid():N}.db");
  private readonly AppDatabase _db;
  private readonly SqliteAppPreferenceStore _store;

  public DeadPreferenceSweepTests()
  {
    _db = new AppDatabase(_dbPath);
    _store = new SqliteAppPreferenceStore(_db);
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
    catch
    {
    }
#pragma warning restore CA1031, S108
  }

  [Fact]
  public async Task Sweep_Removes_The_Named_Dead_Prefixes_And_Nothing_Else()
  {
    CancellationToken ct = TestContext.Current.CancellationToken;
    _ = await _store.SetAsync("smart_selection:openrouter:C:\\ws\\a", "dead-a", ct);
    _ = await _store.SetAsync("smart_selection:openrouter:C:\\ws\\b", "dead-b", ct);
    _ = await _store.SetAsync("subagent_remote_host", "false", ct);
    // Live keys the sweep must never touch: a sibling key that merely CONTAINS the
    // dead text, a prefix-sharing key, and an ordinary preference.
    _ = await _store.SetAsync("active_provider", "openrouter", ct);
    _ = await _store.SetAsync("subagent_max_concurrent_agents", "3", ct);
    _ = await _store.SetAsync("model_choice:openrouter:C:\\ws\\a", "deepseek/deepseek-v4.1-flash-1017", ct);
    _ = await _store.SetAsync("smart_selection_notes", "keep me", ct);
    // The retired toggle is an EXACT key, not a prefix: a longer sibling that merely starts
    // with the same text must survive. A prefix match here would eat a live key.
    _ = await _store.SetAsync("subagent_remote_host_note", "keep me too", ct);

    Result<IReadOnlyList<string>> swept = await DeadPreferenceSweep.SweepAsync(_db, ct);

    Assert.True(swept.IsSuccess, $"[{swept.Error?.Code}] {swept.Error?.Message}");
    Assert.Equal(
        ["smart_selection:openrouter:C:\\ws\\a", "smart_selection:openrouter:C:\\ws\\b", "subagent_remote_host"],
        swept.Value.Order(StringComparer.Ordinal));
    Assert.Null(await _store.GetAsync("smart_selection:openrouter:C:\\ws\\a", ct));
    Assert.Null(await _store.GetAsync("smart_selection:openrouter:C:\\ws\\b", ct));
    Assert.Null(await _store.GetAsync("subagent_remote_host", ct));
    Assert.Equal("openrouter", await _store.GetAsync("active_provider", ct));
    Assert.Equal("3", await _store.GetAsync("subagent_max_concurrent_agents", ct));
    Assert.Equal("deepseek/deepseek-v4.1-flash-1017", await _store.GetAsync("model_choice:openrouter:C:\\ws\\a", ct));
    Assert.Equal("keep me", await _store.GetAsync("smart_selection_notes", ct));
    Assert.Equal("keep me too", await _store.GetAsync("subagent_remote_host_note", ct));
  }

  [Fact]
  public async Task Sweep_With_Nothing_To_Remove_Reports_Empty_And_Logs_Nothing()
  {
    CancellationToken ct = TestContext.Current.CancellationToken;
    _ = await _store.SetAsync("active_provider", "openrouter", ct);

    Result<IReadOnlyList<string>> swept = await DeadPreferenceSweep.SweepAsync(_db, ct);

    Assert.True(swept.IsSuccess, $"[{swept.Error?.Code}] {swept.Error?.Message}");
    Assert.Empty(swept.Value);
    Assert.Null(DeadPreferenceSweep.DescribeOutcome(swept.Value));
  }

  [Fact]
  public async Task Sweep_Is_Idempotent_Second_Run_Finds_Nothing()
  {
    CancellationToken ct = TestContext.Current.CancellationToken;
    _ = await _store.SetAsync("subagent_remote_host", "true", ct);

    Result<IReadOnlyList<string>> first = await DeadPreferenceSweep.SweepAsync(_db, ct);
    Result<IReadOnlyList<string>> second = await DeadPreferenceSweep.SweepAsync(_db, ct);

    Assert.True(first.IsSuccess, $"[{first.Error?.Code}] {first.Error?.Message}");
    Assert.True(second.IsSuccess, $"[{second.Error?.Code}] {second.Error?.Message}");
    _ = Assert.Single(first.Value);
    Assert.Empty(second.Value);
  }

  /// <summary>Issue #310: the todo list's storage was a STATE key (<c>todo/list</c>), not a
  ///     preference, so removing the tool without this sweep would leave an 80 KB row that
  ///     nothing can read and nothing can delete. The removal is REPORTED, because a
  ///     workspace whose todos vanish must not look like data loss.</summary>
  [Fact]
  public async Task Sweep_RemovesTheRetiredTodoStateKey_AndReportsIt()
  {
    CancellationToken ct = TestContext.Current.CancellationToken;
    SqliteStateStore state = new(_db);
    _ = await state.SetKeyCasAsync("ws-a", "todo", "list", "[]", null, ct);
    _ = await state.SetKeyCasAsync("ws-b", "todo", "list", "[]", null, ct);
    // A LIVE namespace that merely starts with the same letters must survive: the match is
    // namespace EQUALITY, never a prefix.
    _ = await state.SetKeyCasAsync("ws-a", "todos", "list", "keep me", null, ct);
    _ = await state.SetKeyCasAsync("ws-a", "sdd.plan", "ledger", "keep me too", null, ct);

    Result<IReadOnlyList<string>> swept = await DeadPreferenceSweep.SweepAsync(_db, ct);

    Assert.True(swept.IsSuccess, $"[{swept.Error?.Code}] {swept.Error?.Message}");
    Assert.Equal(
        ["state:todo/list", "state:todo/list"],
        swept.Value.Order(StringComparer.Ordinal));
    Assert.Null(await state.GetKeyAsync("ws-a", "todo", "list", ct));
    Assert.Null(await state.GetKeyAsync("ws-b", "todo", "list", ct));
    Assert.NotNull(await state.GetKeyAsync("ws-a", "todos", "list", ct));
    Assert.NotNull(await state.GetKeyAsync("ws-a", "sdd.plan", "ledger", ct));

    // The report NAMES what went, so a human reading startup output can tell what happened.
    string? message = DeadPreferenceSweep.DescribeOutcome(swept.Value);
    Assert.NotNull(message);
    Assert.Contains("state:todo/list", message, StringComparison.Ordinal);
  }

  [Fact]
  public void DescribeOutcome_Names_Every_Removed_Key()
  {
    string? message = DeadPreferenceSweep.DescribeOutcome(["smart_selection:x", "subagent_remote_host"]);

    Assert.NotNull(message);
    Assert.Contains("2 row(s)", message, StringComparison.Ordinal);
    Assert.Contains("smart_selection:x", message, StringComparison.Ordinal);
    Assert.Contains("subagent_remote_host", message, StringComparison.Ordinal);
  }
  [Fact]
  public async Task Sweep_Strips_A_Dead_Member_But_Keeps_The_Live_Row()
  {
    // ROUTING IS DELETED, but provider_settings:* survives because it still carries
    // server_tools, plugins, and reasoning_summary. A whole-key sweep would destroy settings
    // the user still wants, so the dead member is stripped from the JSON in place and the
    // row is reported as '<key>#<member>'.
    CancellationToken ct = TestContext.Current.CancellationToken;
    const string key = "provider_settings:openrouter:C:\\ws\\a";
    _ = await _store.SetAsync(key,
        /*lang=json,strict*/"""{"routing":{"data_collection":"deny"},"server_tools":{"openrouter:web_search":true}}""", ct);

    Result<IReadOnlyList<string>> swept = await DeadPreferenceSweep.SweepAsync(_db, ct);

    Assert.True(swept.IsSuccess, $"[{swept.Error?.Code}] {swept.Error?.Message}");
    Assert.Equal([key + "#routing"], swept.Value);

    string? stored = await _store.GetAsync(key, ct);
    Assert.NotNull(stored);
    // The dead member is gone; the live section is untouched.
    Assert.DoesNotContain("routing", stored, StringComparison.Ordinal);
    Assert.Contains("openrouter:web_search", stored, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Sweep_Leaves_An_Unparseable_Payload_Alone()
  {
    // Corrupt storage is an infrastructure fault, not something a startup cleanup should
    // rewrite: a rewrite would destroy the evidence a human needs to diagnose it.
    CancellationToken ct = TestContext.Current.CancellationToken;
    const string key = "provider_settings:openrouter:C:\\ws\\broken";
    _ = await _store.SetAsync(key, "{not json", ct);

    Result<IReadOnlyList<string>> swept = await DeadPreferenceSweep.SweepAsync(_db, ct);

    Assert.True(swept.IsSuccess, $"[{swept.Error?.Code}] {swept.Error?.Message}");
    Assert.Empty(swept.Value);
    Assert.Equal("{not json", await _store.GetAsync(key, ct));
  }

  [Fact]
  public async Task Sweep_Leaves_A_Payload_With_No_Dead_Member_Unrewritten()
  {
    // A clean row must not be rewritten at all - a no-op write would move updated_at and
    // report a change that never happened.
    CancellationToken ct = TestContext.Current.CancellationToken;
    const string key = "provider_settings:openrouter:C:\\ws\\clean";
    const string payload = /*lang=json,strict*/"""{"server_tools":{"openrouter:web_search":true}}""";
    _ = await _store.SetAsync(key, payload, ct);

    Result<IReadOnlyList<string>> swept = await DeadPreferenceSweep.SweepAsync(_db, ct);

    Assert.True(swept.IsSuccess, $"[{swept.Error?.Code}] {swept.Error?.Message}");
    Assert.Empty(swept.Value);
    Assert.Equal(payload, await _store.GetAsync(key, ct));
  }


  [Fact]
  public async Task Sweep_Leaves_The_Rest_Of_The_Database_Intact()
  {
    CancellationToken ct = TestContext.Current.CancellationToken;
    _ = await _store.SetAsync("smart_selection:openrouter:C:\\ws\\a", "{}", ct);

    _ = await DeadPreferenceSweep.SweepAsync(_db, ct);

    using SqliteConnection connection = _db.Open();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='agents';";
    Assert.Equal(1L, Convert.ToInt64(
        await command.ExecuteScalarAsync(TestContext.Current.CancellationToken).ConfigureAwait(true),
        CultureInfo.InvariantCulture));
  }
}
