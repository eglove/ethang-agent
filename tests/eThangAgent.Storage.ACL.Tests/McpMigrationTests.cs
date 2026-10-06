using System.Globalization;
using eThangAgent.AgentDomain;
using eThangAgent.ToolDomain;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>The MCP config migration (V15, issue #103): a V14 database upgrades in place,
///     every earlier table and row is untouched, re-opening is a no-op, and concurrent
///     opens serialize through the process-wide migration gate into one applied schema.
///     The schema carries the scope split (null workspace = global), the per-scope unique
///     name, and the token table keyed by server id.</summary>
public sealed class McpMigrationTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ethang-mcpmig-{Guid.NewGuid():N}.db");

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

  /// <summary>Builds a fully-migrated database carrying real rows, then rolls it back to
  ///     its exact V14 shape: V15's only effects (the mcp_servers and mcp_oauth_tokens
  ///     tables and their indexes) dropped and user_version stamped 14.</summary>
  private async Task SeedV14ShapeAsync()
  {
    AppDatabase db = new(_dbPath);
    // One agent row and one command-run row prove earlier data survives the upgrade.
    AgentRecord root = AgentRecord.Root(AgentId.NewId(), DateTimeOffset.UtcNow, @"C:\workspaces\demo", "openrouter");
    _ = await new SqliteAgentStore(db).SaveAsync(root).ConfigureAwait(false);
    SqliteCommandRunStore commandRuns = new(db, "ws-demo");
    _ = await commandRuns.AddAsync(new CommandRun(0, "git status", 0, false, "clean", DateTimeOffset.UtcNow)).ConfigureAwait(false);

    using SqliteConnection connection = Open();
    using (SqliteCommand dropServers = connection.CreateCommand())
    {
      dropServers.CommandText = "DROP TABLE IF EXISTS mcp_servers;";
      _ = await dropServers.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
    using (SqliteCommand dropTokens = connection.CreateCommand())
    {
      dropTokens.CommandText = "DROP TABLE IF EXISTS mcp_oauth_tokens;";
      _ = await dropTokens.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
    using SqliteCommand version = connection.CreateCommand();
    version.CommandText = "PRAGMA user_version = 14;";
    _ = await version.ExecuteNonQueryAsync().ConfigureAwait(false);
  }

  [Fact]
  public async Task V15_Applies_Over_V14_Leaving_Earlier_Rows_Untouched()
  {
    await SeedV14ShapeAsync().ConfigureAwait(true);

    _ = new AppDatabase(_dbPath); // constructor migrates

    using SqliteConnection connection = Open();
    Assert.Equal(15, Version(connection));
    Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='mcp_servers';"));
    Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='mcp_oauth_tokens';"));
    // Every earlier table and row is untouched.
    Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM agents;"));
    Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM command_runs;"));
  }

  [Fact]
  public async Task V15_Is_Idempotent_Reopening_Is_A_NoOp()
  {
    await SeedV14ShapeAsync().ConfigureAwait(true);

    _ = new AppDatabase(_dbPath); // migrate once
    _ = new AppDatabase(_dbPath); // reopen: no-op

    using SqliteConnection connection = Open();
    Assert.Equal(15, Version(connection));
    // Exactly one mcp_servers table and its two per-scope unique indexes survive a reopen.
    Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='mcp_servers';"));
    Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ux_mcp_servers_scope_name';"));
    Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ux_mcp_servers_ws_scope_name';"));
  }

  [Fact]
  public async Task V15_Is_Idempotent_Under_Concurrent_Open()
  {
    await SeedV14ShapeAsync().ConfigureAwait(true);

    Task<AppDatabase>[] all =
    [
        Task.Run(() => new AppDatabase(_dbPath)),
        Task.Run(() => new AppDatabase(_dbPath)),
    ];
    _ = await Task.WhenAll(all).ConfigureAwait(true);

    using SqliteConnection connection = Open();
    Assert.Equal(15, Version(connection));
    Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='mcp_servers';"));
  }

  [Fact]
  public void Schema_Enforces_Unique_Name_Per_Scope()
  {
    _ = new AppDatabase(_dbPath);

    using SqliteConnection connection = Open();
    InsertServer(connection, "github", workspaceId: null);
    InsertServer(connection, "github", workspaceId: "ws-a");

    // Same name twice in the global scope is refused.
    _ = Assert.Throws<SqliteException>(() => InsertServer(connection, "github", workspaceId: null));
    // Same name twice in one workspace scope is refused.
    _ = Assert.Throws<SqliteException>(() => InsertServer(connection, "github", workspaceId: "ws-a"));
    // A second workspace may hold the same name.
    InsertServer(connection, "github", workspaceId: "ws-b");
    Assert.Equal(3L, Scalar(connection, "SELECT COUNT(*) FROM mcp_servers;"));
  }

  [Fact]
  public async Task Token_Rows_Are_Keyed_By_Server_And_Server_Delete_Cascades()
  {
    _ = new AppDatabase(_dbPath);

    using SqliteConnection connection = Open();
    // The declared ON DELETE CASCADE fires only under per-connection FK enforcement;
    // the store's own delete is transactional regardless (pinned in the store tests).
    using (SqliteCommand fk = connection.CreateCommand())
    {
      fk.CommandText = "PRAGMA foreign_keys = ON;";
      _ = await fk.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    }
    InsertServer(connection, "github", workspaceId: null);
    long serverId = Scalar(connection, "SELECT id FROM mcp_servers WHERE name='github' AND workspace_id IS NULL;");
    InsertToken(connection, serverId, "access-a");
    InsertToken(connection, serverId, "access-b");
    Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM mcp_oauth_tokens;"));

    using (SqliteCommand delete = connection.CreateCommand())
    {
      delete.CommandText = "DELETE FROM mcp_servers WHERE id = $id;";
      _ = delete.Parameters.AddWithValue("$id", serverId);
      _ = await delete.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    // No orphan tokens survive the server row (B3).
    Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM mcp_oauth_tokens;"));
  }

  private static void InsertServer(SqliteConnection connection, string name, string? workspaceId)
  {
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "INSERT INTO mcp_servers (name, transport, command_or_url, args_json, env_json, headers_json, workspace_id, approval_state, pinned_version, created_at) " +
        "VALUES ($name, 'stdio', 'npx', '[]', '{}', '{}', $ws, 'pending', NULL, '2026-10-05T00:00:00Z');";
    _ = command.Parameters.AddWithValue("$name", name);
    _ = command.Parameters.AddWithValue("$ws", (object?)workspaceId ?? DBNull.Value);
    _ = command.ExecuteNonQuery();
  }

  private static void InsertToken(SqliteConnection connection, long serverId, string accessToken)
  {
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "INSERT INTO mcp_oauth_tokens (server_id, access_token, refresh_token, expires_at) VALUES ($server, $access, NULL, NULL);";
    _ = command.Parameters.AddWithValue("$server", serverId);
    _ = command.Parameters.AddWithValue("$access", accessToken);
    _ = command.ExecuteNonQuery();
  }

  private SqliteConnection Open()
  {
    SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
    connection.Open();
    return connection;
  }

  private static int Version(SqliteConnection connection)
  {
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "PRAGMA user_version;";
    return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
  }

  private static long Scalar(SqliteConnection connection, string sql)
  {
    using SqliteCommand command = connection.CreateCommand();
#pragma warning disable CA2100 // constant test-only SQL
    command.CommandText = sql;
#pragma warning restore CA2100
    return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
  }
}
