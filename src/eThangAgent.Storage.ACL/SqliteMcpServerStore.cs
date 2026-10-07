using System.Globalization;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain.Mcp;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>SQLite-backed persistence for configured MCP servers and their OAuth tokens
///     (issues #102/#103) over the V15 tables. Scope visibility is the reader's
///     contract: a null-workspace row is global and visible to every workspace read;
///     a workspace row is visible only to its own workspace (B2). Name is unique per
///     scope, enforced by the schema's partial unique indexes and surfaced as
///     McpDuplicateName. Deleting a server removes its token rows in the same
///     transaction, so no orphan tokens survive (B3). Expected failures flow as
///     DomainErrors through Result; storage faults surface as StorageUnavailable,
///     never exceptions.</summary>
public sealed class SqliteMcpServerStore(AppDatabase database) : IMcpServerStore
{
  private readonly AppDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

  public async Task<Result<IReadOnlyList<McpServerConfig>>> ListAsync(string workspaceId, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = """
          SELECT id, name, transport, command_or_url, args_json, env_json, headers_json, workspace_id, approval_state, pinned_version, created_at, gate_mode
          FROM mcp_servers
          WHERE workspace_id IS NULL OR workspace_id = @ws
          ORDER BY name;
          """;
      _ = command.Parameters.AddWithValue("@ws", workspaceId);
      List<McpServerConfig> servers = [];
      using (SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
      {
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
          servers.Add(await MapAsync(reader, ct).ConfigureAwait(false));
        }
      }
      return Result.Success<IReadOnlyList<McpServerConfig>>(servers);
    }
    catch (SqliteException ex)
    {
      return Result.Failure<IReadOnlyList<McpServerConfig>>(Unavailable(ex));
    }
  }

  public async Task<Result<McpServerConfig>> GetAsync(int id, string workspaceId, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      return await ReadAsync(connection, null, id, workspaceId, ct).ConfigureAwait(false);
    }
    catch (SqliteException ex)
    {
      return Result.Failure<McpServerConfig>(Unavailable(ex));
    }
  }

  public async Task<Result<McpServerConfig>> AddAsync(McpServerConfig server, CancellationToken ct = default)
  {
    Validate(server);
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
      await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
#pragma warning restore CA2007

      Result<McpServerConfig> inserted = await InsertAsync(connection, transaction, server, ct).ConfigureAwait(false);
      if (!inserted.IsSuccess)
      {
        await transaction.RollbackAsync(ct).ConfigureAwait(false);
        return inserted;
      }

      await transaction.CommitAsync(ct).ConfigureAwait(false);
      return inserted;
    }
    catch (SqliteException ex)
    {
      return Result.Failure<McpServerConfig>(Unavailable(ex));
    }
  }

  public async Task<Result<McpServerConfig>> UpdateAsync(McpServerConfig server, CancellationToken ct = default)
  {
    Validate(server);
    if (server.Id <= 0)
    {
      throw new ArgumentException("Server id must be a positive store-assigned id.", nameof(server));
    }

    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
      await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
#pragma warning restore CA2007

      // The duplicate-name check runs inside the transaction so a concurrent rename
      // cannot slip past it between check and update.
      if (await NameTakenAsync(connection, transaction, server, ct).ConfigureAwait(false))
      {
        await transaction.RollbackAsync(ct).ConfigureAwait(false);
        return Result.Failure<McpServerConfig>(DuplicateName(server));
      }

      using (SqliteCommand update = connection.CreateCommand())
      {
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE mcp_servers SET
                name = @name,
                transport = @transport,
                command_or_url = @commandOrUrl,
                args_json = @args,
                env_json = @env,
                headers_json = @headers,
                approval_state = @approval,
                pinned_version = @pinned,
                gate_mode = @gate
            WHERE id = @id;
            """;
        AddServerFields(update, server);
        _ = update.Parameters.AddWithValue("@id", server.Id);
        if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
        {
          await transaction.RollbackAsync(ct).ConfigureAwait(false);
          return Result.Failure<McpServerConfig>(NotFound(server.Id));
        }
      }

      await transaction.CommitAsync(ct).ConfigureAwait(false);
      return Result.Success(server);
    }
    catch (SqliteException ex)
    {
      return Result.Failure<McpServerConfig>(Unavailable(ex));
    }
  }

  public async Task<Result<bool>> DeleteAsync(int id, string workspaceId, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
      await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
#pragma warning restore CA2007

      // Scope check first: an id outside the reading workspace's scope fails
      // McpServerNotFound rather than deleting another workspace's row. An unknown
      // id inside the scope is an honest false, not a failure.
      Result<McpServerConfig> existing = await ReadAsync(connection, transaction, id, workspaceId, ct).ConfigureAwait(false);
      if (!existing.IsSuccess)
      {
        // Distinguish unknown id (honest false) from foreign scope (McpServerNotFound)
        // BEFORE the rollback - a command cannot ride a dead transaction.
        bool rowExists = await ServerExistsAsync(connection, transaction, id, ct).ConfigureAwait(false);
        await transaction.RollbackAsync(ct).ConfigureAwait(false);
        return existing.Error.Code == "McpServerNotFound" && !rowExists
            ? Result.Success(false)
            : Result.Failure<bool>(existing.Error);
      }

      using (SqliteCommand deleteTokens = connection.CreateCommand())
      {
        deleteTokens.Transaction = transaction;
        deleteTokens.CommandText = "DELETE FROM mcp_oauth_tokens WHERE server_id = @id;";
        _ = deleteTokens.Parameters.AddWithValue("@id", id);
        _ = await deleteTokens.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      }
      using (SqliteCommand deleteDecisions = connection.CreateCommand())
      {
        deleteDecisions.Transaction = transaction;
        deleteDecisions.CommandText = "DELETE FROM mcp_decisions WHERE server_id = @id;";
        _ = deleteDecisions.Parameters.AddWithValue("@id", id);
        _ = await deleteDecisions.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      }
      using (SqliteCommand deleteServer = connection.CreateCommand())
      {
        deleteServer.Transaction = transaction;
        deleteServer.CommandText = "DELETE FROM mcp_servers WHERE id = @id;";
        _ = deleteServer.Parameters.AddWithValue("@id", id);
        _ = await deleteServer.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      }

      await transaction.CommitAsync(ct).ConfigureAwait(false);
      return Result.Success(true);
    }
    catch (SqliteException ex)
    {
      return Result.Failure<bool>(Unavailable(ex));
    }
  }

  public async Task<Result<McpOAuthTokens>> SaveTokensAsync(int serverId, McpOAuthTokens tokens, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(tokens);
    ArgumentException.ThrowIfNullOrWhiteSpace(tokens.AccessToken);
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
      await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
#pragma warning restore CA2007

      if (!await ServerExistsAsync(connection, transaction, serverId, ct).ConfigureAwait(false))
      {
        await transaction.RollbackAsync(ct).ConfigureAwait(false);
        return Result.Failure<McpOAuthTokens>(NotFound(serverId));
      }

      // Replace is whole-row: one row per server, overwritten together.
      using (SqliteCommand delete = connection.CreateCommand())
      {
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM mcp_oauth_tokens WHERE server_id = @id;";
        _ = delete.Parameters.AddWithValue("@id", serverId);
        _ = await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      }
      using (SqliteCommand insert = connection.CreateCommand())
      {
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO mcp_oauth_tokens (server_id, access_token, refresh_token, expires_at)
            VALUES (@id, @access, @refresh, @expires);
            """;
        _ = insert.Parameters.AddWithValue("@id", serverId);
        _ = insert.Parameters.AddWithValue("@access", tokens.AccessToken);
        _ = insert.Parameters.AddWithValue("@refresh", (object?)tokens.RefreshToken ?? DBNull.Value);
        _ = insert.Parameters.AddWithValue("@expires", tokens.ExpiresAt is { } expiry
            ? expiry.ToString("O", CultureInfo.InvariantCulture)
            : DBNull.Value);
        _ = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      }

      await transaction.CommitAsync(ct).ConfigureAwait(false);
      return Result.Success(tokens);
    }
    catch (SqliteException ex)
    {
      return Result.Failure<McpOAuthTokens>(Unavailable(ex));
    }
  }

  public async Task<Result<McpOAuthTokens?>> GetTokensAsync(int serverId, CancellationToken ct = default)
  {
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = """
          SELECT access_token, refresh_token, expires_at
          FROM mcp_oauth_tokens WHERE server_id = @id;
          """;
      _ = command.Parameters.AddWithValue("@id", serverId);
      using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
      if (!await reader.ReadAsync(ct).ConfigureAwait(false))
      {
        return Result.Success<McpOAuthTokens?>(null);
      }

      string? refresh = await reader.IsDBNullAsync(1, ct).ConfigureAwait(false) ? null : reader.GetString(1);
      DateTimeOffset? expires = await reader.IsDBNullAsync(2, ct).ConfigureAwait(false)
          ? null
          : DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture);
      return Result.Success<McpOAuthTokens?>(new McpOAuthTokens(reader.GetString(0), refresh, expires));
    }
    catch (SqliteException ex)
    {
      return Result.Failure<McpOAuthTokens?>(Unavailable(ex));
    }
  }

  public async Task<Result<bool>> AppendDecisionAsync(int serverId, string decision, string? detail, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(decision);
    try
    {
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
      await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
#pragma warning restore CA2007

      if (!await ServerExistsAsync(connection, transaction, serverId, ct).ConfigureAwait(false))
      {
        await transaction.RollbackAsync(ct).ConfigureAwait(false);
        return Result.Failure<bool>(NotFound(serverId));
      }

      using (SqliteCommand insert = connection.CreateCommand())
      {
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO mcp_decisions (server_id, decision, detail, created_at)
            VALUES (@id, @decision, @detail, @created);
            """;
        _ = insert.Parameters.AddWithValue("@id", serverId);
        _ = insert.Parameters.AddWithValue("@decision", decision);
        _ = insert.Parameters.AddWithValue("@detail", (object?)detail ?? DBNull.Value);
        _ = insert.Parameters.AddWithValue("@created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        _ = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      }

      await transaction.CommitAsync(ct).ConfigureAwait(false);
      return Result.Success(true);
    }
    catch (SqliteException ex)
    {
      return Result.Failure<bool>(Unavailable(ex));
    }
  }

  public async Task<Result<IReadOnlyList<McpDecision>>> ListDecisionsAsync(int serverId, int take, CancellationToken ct = default)
  {
    try
    {
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = """
          SELECT server_id, decision, detail, created_at
          FROM mcp_decisions WHERE server_id = @id
          ORDER BY id
          LIMIT @take;
          """;
      _ = command.Parameters.AddWithValue("@id", serverId);
      _ = command.Parameters.AddWithValue("@take", take);
      List<McpDecision> decisions = [];
      using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
      while (await reader.ReadAsync(ct).ConfigureAwait(false))
      {
        decisions.Add(new McpDecision(
            reader.GetInt32(0),
            reader.GetString(1),
            await reader.IsDBNullAsync(2, ct).ConfigureAwait(false) ? null : reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture)));
      }

      return Result.Success<IReadOnlyList<McpDecision>>(decisions);
    }
    catch (SqliteException ex)
    {
      return Result.Failure<IReadOnlyList<McpDecision>>(Unavailable(ex));
    }
  }

  private static async Task<Result<McpServerConfig>> InsertAsync(
      SqliteConnection connection, SqliteTransaction transaction, McpServerConfig server, CancellationToken ct)
  {
    if (await NameTakenAsync(connection, transaction, server, ct).ConfigureAwait(false))
    {
      return Result.Failure<McpServerConfig>(DuplicateName(server));
    }

    int id;
    using (SqliteCommand insert = connection.CreateCommand())
    {
      insert.Transaction = transaction;
      insert.CommandText = """
          INSERT INTO mcp_servers (name, transport, command_or_url, args_json, env_json, headers_json, workspace_id, approval_state, pinned_version, created_at, gate_mode)
          VALUES (@name, @transport, @commandOrUrl, @args, @env, @headers, @ws, @approval, @pinned, @created, @gate);
          """;
      AddServerFields(insert, server);
      _ = insert.Parameters.AddWithValue("@ws", (object?)server.WorkspaceId ?? DBNull.Value);
      _ = insert.Parameters.AddWithValue("@created", server.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
      try
      {
        _ = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      }
      catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
      {
        // The partial unique index refused the name within this scope.
        return Result.Failure<McpServerConfig>(DuplicateName(server));
      }
    }

    using (SqliteCommand idCommand = connection.CreateCommand())
    {
      idCommand.Transaction = transaction;
      idCommand.CommandText = "SELECT last_insert_rowid();";
      id = Convert.ToInt32(await idCommand.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }
    return Result.Success(server with { Id = id });
  }

  /// <summary>Whether the candidate name is already taken within the candidate's own
  ///     scope - the (name, workspace) pair the schema's partial unique indexes pin.</summary>
  private static async Task<bool> NameTakenAsync(
      SqliteConnection connection, SqliteTransaction transaction, McpServerConfig server, CancellationToken ct)
  {
    using SqliteCommand command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = """
        SELECT COUNT(*) FROM mcp_servers
        WHERE name = @name AND workspace_id IS @ws AND id != @id;
        """;
    _ = command.Parameters.AddWithValue("@name", server.Name);
    _ = command.Parameters.AddWithValue("@ws", (object?)server.WorkspaceId ?? DBNull.Value);
    _ = command.Parameters.AddWithValue("@id", server.Id);
    long count = Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    return count > 0;
  }

  private static async Task<bool> ServerExistsAsync(
      SqliteConnection connection, SqliteTransaction transaction, int serverId, CancellationToken ct)
  {
    using SqliteCommand command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = "SELECT COUNT(*) FROM mcp_servers WHERE id = @id;";
    _ = command.Parameters.AddWithValue("@id", serverId);
    long count = Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    return count > 0;
  }

  private static async Task<Result<McpServerConfig>> ReadAsync(
      SqliteConnection connection, SqliteTransaction? transaction, int id, string workspaceId, CancellationToken ct)
  {
    using SqliteCommand command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = """
        SELECT id, name, transport, command_or_url, args_json, env_json, headers_json, workspace_id, approval_state, pinned_version, created_at, gate_mode
        FROM mcp_servers
        WHERE id = @id AND (workspace_id IS NULL OR workspace_id = @ws);
        """;
    _ = command.Parameters.AddWithValue("@id", id);
    _ = command.Parameters.AddWithValue("@ws", workspaceId);
    using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
    return !await reader.ReadAsync(ct).ConfigureAwait(false)
        ? Result.Failure<McpServerConfig>(NotFound(id))
        : Result.Success(await MapAsync(reader, ct).ConfigureAwait(false));
  }

  private static async Task<McpServerConfig> MapAsync(SqliteDataReader reader, CancellationToken ct)
  {
    return new McpServerConfig(
        reader.GetInt32(0),
        reader.GetString(1),
        ParseTransport(reader.GetString(2)),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        await reader.IsDBNullAsync(7, ct).ConfigureAwait(false) ? null : reader.GetString(7),
        ParseApproval(reader.GetString(8)),
        await reader.IsDBNullAsync(9, ct).ConfigureAwait(false) ? null : reader.GetString(9),
        DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture),
        ParseGate(reader.GetString(11)));
  }

  private static McpGateMode ParseGate(string value)
      => value switch
      {
        "none" => McpGateMode.None,
        "mutating" => McpGateMode.Mutating,
        _ => throw new InvalidOperationException($"corrupt mcp_servers row: unknown gate mode '{value}'."),
      };

  private static string GateText(McpGateMode mode)
      => mode switch
      {
        McpGateMode.None => "none",
        McpGateMode.Mutating => "mutating",
        _ => "none",
      };

  private static McpTransport ParseTransport(string value)
      => value switch
      {
        "stdio" => McpTransport.Stdio,
        "http" => McpTransport.Http,
        _ => throw new InvalidOperationException($"corrupt mcp_servers row: unknown transport '{value}'."),
      };

  private static string ApprovalText(McpApprovalState state)
      => state switch
      {
        McpApprovalState.Pending => "pending",
        McpApprovalState.Approved => "approved",
        McpApprovalState.Revoked => "revoked",
        _ => "revoked",
      };


  private static McpApprovalState ParseApproval(string value)
      => value switch
      {
        "pending" => McpApprovalState.Pending,
        "approved" => McpApprovalState.Approved,
        "revoked" => McpApprovalState.Revoked,
        _ => throw new InvalidOperationException($"corrupt mcp_servers row: unknown approval state '{value}'."),
      };

  private static void AddServerFields(SqliteCommand command, McpServerConfig server)
  {
    _ = command.Parameters.AddWithValue("@name", server.Name);
    _ = command.Parameters.AddWithValue("@transport", server.Transport == McpTransport.Stdio ? "stdio" : "http");
    _ = command.Parameters.AddWithValue("@commandOrUrl", server.CommandOrUrl);
    _ = command.Parameters.AddWithValue("@args", server.ArgsJson);
    _ = command.Parameters.AddWithValue("@env", server.EnvJson);
    _ = command.Parameters.AddWithValue("@headers", server.HeadersJson);
    _ = command.Parameters.AddWithValue("@approval", ApprovalText(server.ApprovalState));
    _ = command.Parameters.AddWithValue("@pinned", (object?)server.PinnedVersion ?? DBNull.Value);
    _ = command.Parameters.AddWithValue("@gate", GateText(server.GateMode));
  }

  private static void Validate(McpServerConfig server)
  {
    ArgumentNullException.ThrowIfNull(server);
    ArgumentException.ThrowIfNullOrWhiteSpace(server.Name);
    ArgumentException.ThrowIfNullOrWhiteSpace(server.CommandOrUrl);
  }

  private static DomainError NotFound(int id)
      => new("McpServerNotFound", $"No MCP server with id {id} is visible in this workspace's scope.");

  private static DomainError DuplicateName(McpServerConfig server)
      => new("McpDuplicateName",
          $"An MCP server named '{server.Name}' is already configured in the {(server.WorkspaceId is null ? "global" : server.WorkspaceId)} scope.");

  private static DomainError Unavailable(SqliteException ex)
      => new("StorageUnavailable", $"the MCP server store could not complete the operation: {ex.Message}");
}
