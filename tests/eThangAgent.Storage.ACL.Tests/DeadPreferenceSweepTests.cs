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
