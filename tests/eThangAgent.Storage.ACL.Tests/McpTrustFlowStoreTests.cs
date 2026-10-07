#pragma warning disable JSON002 // deliberate JSON fixtures in test data
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain.Mcp;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>The trust-flow persistence (issues #107/#109): the approval decision log
///     (every human approve/revoke and every gate denial lands as a row) and the
///     per-server gate mode column (V16).</summary>
public sealed class McpTrustFlowStoreTests : IDisposable
{
  private const string GlobalWs = "global-scope";
  private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ethang-mcptf-{Guid.NewGuid():N}.db");
  private readonly AppDatabase _db;
  private readonly SqliteMcpServerStore _store;

  public McpTrustFlowStoreTests()
  {
    _db = new AppDatabase(_dbPath);
    _store = new SqliteMcpServerStore(_db);
  }

  public void Dispose()
  {
    GC.SuppressFinalize(this);
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

  private static McpServerConfig Approved() => new(0, "github", McpTransport.Stdio, "npx", "[]", "{}", "{}",
      null, McpApprovalState.Approved, null, new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));

  private async Task<McpServerConfig> AddAsync(McpServerConfig draft)
  {
    Result<McpServerConfig> added = await _store.AddAsync(draft, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(added.IsSuccess, added.Error?.Message);
    return added.Value;
  }

  [Fact]
  public async Task GateMode_RoundTrips_DefaultNone()
  {
    McpServerConfig stored = await AddAsync(Approved()).ConfigureAwait(true);
    Assert.Equal(McpGateMode.None, stored.GateMode);

    Result<McpServerConfig> updated = await _store.UpdateAsync(stored with { GateMode = McpGateMode.Mutating },
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(updated.IsSuccess, updated.Error?.Message);
    Assert.Equal(McpGateMode.Mutating, updated.Value.GateMode);

    Result<McpServerConfig> back = await _store.GetAsync(stored.Id, GlobalWs, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(back.IsSuccess, back.Error?.Message);
    Assert.Equal(McpGateMode.Mutating, back.Value.GateMode);
  }

  [Fact]
  public async Task AppendDecision_Persists_Decisions_InOrder()
  {
    McpServerConfig stored = await AddAsync(Approved()).ConfigureAwait(true);

    Result<bool> first = await _store.AppendDecisionAsync(stored.Id, "approved", "the user clicked approve",
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(first.IsSuccess, first.Error?.Message);
    _ = await _store.AppendDecisionAsync(stored.Id, "gate-denied", "delete_repo is mutating",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Result<IReadOnlyList<McpDecision>> log = await _store.ListDecisionsAsync(stored.Id, 10, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(log.IsSuccess, log.Error?.Message);
    Assert.Equal(2, log.Value.Count);
    Assert.Equal("approved", log.Value[0].Decision);
    Assert.Equal("the user clicked approve", log.Value[0].Detail);
    Assert.Equal("gate-denied", log.Value[1].Decision);
  }

  [Fact]
  public async Task AppendDecision_UnknownServer_Fails()
  {
    Result<bool> result = await _store.AppendDecisionAsync(999, "approved", null,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(result.IsSuccess);
    Assert.Equal("McpServerNotFound", result.Error.Code);
  }

  [Fact]
  public async Task Removing_Server_Leaves_Decisions_And_Tokens_Gone()
  {
    McpServerConfig stored = await AddAsync(Approved()).ConfigureAwait(true);
    _ = await _store.SaveTokensAsync(stored.Id, new McpOAuthTokens("a", null, null), TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = await _store.AppendDecisionAsync(stored.Id, "approved", null, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Result<bool> deleted = await _store.DeleteAsync(stored.Id, GlobalWs, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(deleted.IsSuccess && deleted.Value);

    Result<IReadOnlyList<McpDecision>> log = await _store.ListDecisionsAsync(stored.Id, 10, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(log.IsSuccess);
    Assert.Empty(log.Value);
    Result<McpOAuthTokens?> tokens = await _store.GetTokensAsync(stored.Id, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(tokens.IsSuccess);
    Assert.Null(tokens.Value);
  }
}
