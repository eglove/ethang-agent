#pragma warning disable JSON002 // deliberate JSON fixtures in test data
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain.Mcp;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>The MCP server store contract (issue #103): scope visibility (B2 - a global
///     row is visible from every workspace, a workspace row only from its own),
///     name-per-scope uniqueness, the transactional token cascade (B3), and strict
///     input validation at the boundary.</summary>
public sealed class SqliteMcpServerStoreTests : IDisposable
{
  private const string GlobalWs = "global-scope";
  private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ethang-mcpstore-{Guid.NewGuid():N}.db");
  private readonly AppDatabase _db;
  private readonly SqliteMcpServerStore _store;

  public SqliteMcpServerStoreTests()
  {
    _db = new AppDatabase(_dbPath);
    _store = new SqliteMcpServerStore(_db);
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

  private static McpServerConfig Global(string name, McpApprovalState state = McpApprovalState.Pending, string? pinned = null) =>
      new(0, name, McpTransport.Stdio, "npx", "[\"-y\",\"server.js\"]", "{}", "{}", null, state, pinned,
          new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));

  private static McpServerConfig Workspace(string ws, string name) =>
      new(0, name, McpTransport.Http, "https://example.invalid/mcp", "[]", "{}", "{\"x\":\"1\"}", ws,
          McpApprovalState.Approved, "1.2.3", new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));

  private async Task<McpServerConfig> AddAsync(McpServerConfig draft)
  {
    Result<McpServerConfig> added = await _store.AddAsync(draft, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(added.IsSuccess, added.Error?.Message);
    return added.Value;
  }

  private async Task<IReadOnlyList<McpServerConfig>> ListAsync(string ws)
  {
    Result<IReadOnlyList<McpServerConfig>> listed = await _store.ListAsync(ws, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(listed.IsSuccess, listed.Error?.Message);
    return listed.Value;
  }

  [Fact]
  public async Task Add_AssignsId_And_RoundTrips_All_Fields()
  {
    McpServerConfig stored = await AddAsync(Global("github", McpApprovalState.Revoked, "2.1.0")).ConfigureAwait(true);

    Assert.NotEqual(0, stored.Id);
    Result<McpServerConfig> back = await _store.GetAsync(stored.Id, GlobalWs, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(back.IsSuccess, back.Error?.Message);
    Assert.Equal("github", back.Value.Name);
    Assert.Equal(McpTransport.Stdio, back.Value.Transport);
    Assert.Equal("npx", back.Value.CommandOrUrl);
    Assert.Equal("[\"-y\",\"server.js\"]", back.Value.ArgsJson);
    Assert.Equal("{}", back.Value.EnvJson);
    Assert.Equal("{}", back.Value.HeadersJson);
    Assert.Null(back.Value.WorkspaceId);
    Assert.Equal(McpApprovalState.Revoked, back.Value.ApprovalState);
    Assert.Equal("2.1.0", back.Value.PinnedVersion);
    Assert.Equal(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero), back.Value.CreatedAt);
  }

  [Fact]
  public async Task List_Visible_From_Every_Workspace_When_Global_And_Only_Own_When_WorkspaceScoped()
  {
    _ = await AddAsync(Global("shared")).ConfigureAwait(true);
    _ = await AddAsync(Workspace("ws-a", "alpha")).ConfigureAwait(true);
    _ = await AddAsync(Workspace("ws-b", "beta")).ConfigureAwait(true);

    // B2: the global row is visible from two workspace contexts.
    IReadOnlyList<McpServerConfig> a = await ListAsync("ws-a").ConfigureAwait(true);
    IReadOnlyList<McpServerConfig> b = await ListAsync("ws-b").ConfigureAwait(true);
    Assert.Equal("alpha", a[0].Name);
    Assert.Equal("shared", a[1].Name);
    Assert.Equal("beta", b[0].Name);
    Assert.Equal("shared", b[1].Name);
  }

  [Fact]
  public async Task Get_UnknownId_FailsNotFound()
  {
    Result<McpServerConfig> r = await _store.GetAsync(42, GlobalWs, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(r.IsSuccess);
    Assert.Equal("McpServerNotFound", r.Error.Code);
  }

  [Fact]
  public async Task Get_AnotherWorkspacesRow_FailsNotFound_NoScopeLeak()
  {
    McpServerConfig stored = await AddAsync(Workspace("ws-a", "alpha")).ConfigureAwait(true);

    Result<McpServerConfig> leaked = await _store.GetAsync(stored.Id, "ws-b", TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(leaked.IsSuccess);
    Assert.Equal("McpServerNotFound", leaked.Error.Code);
  }

  [Fact]
  public async Task Add_DuplicateName_In_Same_Scope_Fails()
  {
    _ = await AddAsync(Global("github")).ConfigureAwait(true);
    _ = await AddAsync(Workspace("ws-a", "github")).ConfigureAwait(true);

    Result<McpServerConfig> duplicateGlobal = await _store.AddAsync(Global("github"), TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(duplicateGlobal.IsSuccess);
    Assert.Equal("McpDuplicateName", duplicateGlobal.Error.Code);

    Result<McpServerConfig> duplicateWs = await _store.AddAsync(Workspace("ws-a", "github"), TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(duplicateWs.IsSuccess);
    Assert.Equal("McpDuplicateName", duplicateWs.Error.Code);

    // A second workspace may hold the same name.
    _ = await AddAsync(Workspace("ws-b", "github")).ConfigureAwait(true);
    Assert.Equal(2, (await ListAsync("ws-b").ConfigureAwait(true)).Count);
  }

  [Fact]
  public async Task Update_Changes_Mutable_Fields_And_Keeps_Scope()
  {
    McpServerConfig stored = await AddAsync(Workspace("ws-a", "alpha")).ConfigureAwait(true);

    McpServerConfig updated = stored with { Name = "renamed", ApprovalState = McpApprovalState.Revoked, PinnedVersion = "9.9.9" };
    Result<McpServerConfig> saved = await _store.UpdateAsync(updated, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(saved.IsSuccess, saved.Error?.Message);

    Result<McpServerConfig> back = await _store.GetAsync(stored.Id, "ws-a", TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(back.IsSuccess, back.Error?.Message);
    Assert.Equal("renamed", back.Value.Name);
    Assert.Equal(McpApprovalState.Revoked, back.Value.ApprovalState);
    Assert.Equal("9.9.9", back.Value.PinnedVersion);
    Assert.Equal("ws-a", back.Value.WorkspaceId);
  }

  [Fact]
  public async Task Update_UnknownId_FailsNotFound()
  {
    Result<McpServerConfig> r = await _store.UpdateAsync(Global("ghost") with { Id = 42 }, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(r.IsSuccess);
    Assert.Equal("McpServerNotFound", r.Error.Code);
  }

  [Fact]
  public async Task Update_Renaming_To_A_Taken_Name_Fails()
  {
    _ = await AddAsync(Global("taken")).ConfigureAwait(true);
    McpServerConfig stored = await AddAsync(Global("free")).ConfigureAwait(true);

    Result<McpServerConfig> r = await _store.UpdateAsync(stored with { Name = "taken" }, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(r.IsSuccess);
    Assert.Equal("McpDuplicateName", r.Error.Code);
  }

  [Fact]
  public async Task Delete_Removes_Server_And_Tokens_In_One_Transaction()
  {
    McpServerConfig stored = await AddAsync(Global("github")).ConfigureAwait(true);
    _ = await _store.SaveTokensAsync(stored.Id, new McpOAuthTokens("access", "refresh",
        new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)), TestContext.Current.CancellationToken).ConfigureAwait(true);

    Result<bool> deleted = await _store.DeleteAsync(stored.Id, GlobalWs, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(deleted.IsSuccess, deleted.Error?.Message);
    Assert.True(deleted.Value);

    // No orphan tokens survive (B3), and the row is gone.
    Assert.Equal(0L, CountTokenRows());
    Result<McpServerConfig> gone = await _store.GetAsync(stored.Id, GlobalWs, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(gone.IsSuccess);
  }

  [Fact]
  public async Task Delete_UnknownId_ReportsFalse_Delete_AnotherWorkspacesRow_Fails()
  {
    McpServerConfig stored = await AddAsync(Workspace("ws-a", "alpha")).ConfigureAwait(true);

    Result<bool> unknown = await _store.DeleteAsync(42, GlobalWs, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(unknown.IsSuccess);
    Assert.False(unknown.Value);

    Result<bool> foreign = await _store.DeleteAsync(stored.Id, "ws-b", TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(foreign.IsSuccess);
    Assert.Equal("McpServerNotFound", foreign.Error.Code);
    _ = Assert.Single(await ListAsync("ws-a").ConfigureAwait(true));
  }

  [Fact]
  public async Task Tokens_RoundTrip_And_Replace_Whole_Row()
  {
    McpServerConfig stored = await AddAsync(Global("github")).ConfigureAwait(true);
    DateTimeOffset expiry = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

    _ = await _store.SaveTokensAsync(stored.Id, new McpOAuthTokens("access-1", "refresh-1", expiry), TestContext.Current.CancellationToken).ConfigureAwait(true);
    Result<McpOAuthTokens?> first = await _store.GetTokensAsync(stored.Id, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(first.IsSuccess, first.Error?.Message);
    Assert.NotNull(first.Value);
    Assert.Equal("access-1", first.Value.AccessToken);
    Assert.Equal("refresh-1", first.Value.RefreshToken);
    Assert.Equal(expiry, first.Value.ExpiresAt);

    // Replace is whole-row: the second save overwrites both tokens and the expiry.
    _ = await _store.SaveTokensAsync(stored.Id, new McpOAuthTokens("access-2", null, null), TestContext.Current.CancellationToken).ConfigureAwait(true);
    Result<McpOAuthTokens?> second = await _store.GetTokensAsync(stored.Id, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(second.IsSuccess, second.Error?.Message);
    Assert.Equal("access-2", second.Value.AccessToken);
    Assert.Null(second.Value.RefreshToken);
    Assert.Null(second.Value.ExpiresAt);
  }

  [Fact]
  public async Task Tokens_UnknownServer_FailsNotFound_And_UnstoredServer_ReturnsNull()
  {
    Result<McpOAuthTokens?> none = await _store.GetTokensAsync(42, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(none.IsSuccess);
    Assert.Null(none.ValueOrNull);

    Result<McpOAuthTokens> save = await _store.SaveTokensAsync(42, new McpOAuthTokens("a", null, null), TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(save.IsSuccess);
    Assert.Equal("McpServerNotFound", save.Error.Code);
  }

  [Fact]
  public async Task Blank_Inputs_Are_Rejected()
  {
    _ = await Assert.ThrowsAsync<ArgumentException>(() => _store.ListAsync(" ", TestContext.Current.CancellationToken)).ConfigureAwait(true);
    _ = await Assert.ThrowsAsync<ArgumentException>(() => _store.AddAsync(Global(" "), TestContext.Current.CancellationToken)).ConfigureAwait(true);
    _ = await Assert.ThrowsAsync<ArgumentException>(() => _store.AddAsync(Global("x") with { CommandOrUrl = " " }, TestContext.Current.CancellationToken)).ConfigureAwait(true);
    _ = await Assert.ThrowsAnyAsync<ArgumentException>(() => _store.UpdateAsync(null!, TestContext.Current.CancellationToken)).ConfigureAwait(true);
    _ = await Assert.ThrowsAsync<ArgumentException>(() => _store.DeleteAsync(1, " ", TestContext.Current.CancellationToken)).ConfigureAwait(true);
    _ = await Assert.ThrowsAnyAsync<ArgumentException>(() => _store.SaveTokensAsync(1, null!, TestContext.Current.CancellationToken)).ConfigureAwait(true);
    _ = await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveTokensAsync(1, new McpOAuthTokens(" ", null, null), TestContext.Current.CancellationToken)).ConfigureAwait(true);
  }

  private long CountTokenRows()
  {
    using SqliteConnection connection = _db.Open();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM mcp_oauth_tokens;";
    return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
  }
}
