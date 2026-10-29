using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>SQLite-backed chain persistence (issue #300): one chains row per chain (global
///     autoincrement id, bound to the workspace given at construction), one chain_steps row
///     per step keyed by (chain_id, position). Every statement filters on the bound
///     workspace, so another workspace's ids are invisible - a Get or Save on one fails
///     ChainNotFound rather than leaking. Expected failures flow as DomainErrors
///     (ChainNotFound, VersionConflict) through Result; storage faults surface as a
///     StorageUnavailable failure, never an exception.
///     <para>The CAS contract and the corrupt-row containment are the plan store's,
///     deliberately: the chain is the plan's sibling, so a second persistence idiom in the
///     same database would be a second thing to get wrong.</para></summary>
public sealed class SqliteChainStore(AppDatabase database, string workspaceId) : IChainStore
{
  private readonly AppDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
  private readonly string _workspaceId = ValidateWorkspace(workspaceId);

  public async Task<Result<Chain>> CreateAsync(Chain draft, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(draft);
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
      await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
#pragma warning restore CA2007

      using (SqliteCommand insert = connection.CreateCommand())
      {
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO chains (workspace_id, plan_id, plan_version, title, status, park_reason,
                parked_at_position, session_id, created_at, updated_at, version)
            VALUES (@ws, @planId, @planVersion, @title, @status, @parkReason, @parkedAt, @session,
                @created, @updated, 1);
            """;
        Add(insert, "@ws", _workspaceId);
        Add(insert, "@planId", draft.PlanId);
        Add(insert, "@planVersion", draft.PlanVersion);
        Add(insert, "@title", draft.Title);
        Add(insert, "@status", draft.Status.ToString());
        Add(insert, "@parkReason", draft.ParkReason);
        Add(insert, "@parkedAt", draft.ParkedAtPosition);
        Add(insert, "@session", draft.SessionId);
        Add(insert, "@created", Format(draft.CreatedAt));
        Add(insert, "@updated", Format(draft.UpdatedAt));
        _ = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      }

      int id;
      using (SqliteCommand idCommand = connection.CreateCommand())
      {
        idCommand.Transaction = transaction;
        idCommand.CommandText = "SELECT last_insert_rowid();";
        id = Convert.ToInt32(await idCommand.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
      }

      foreach (ChainStep step in draft.Steps)
      {
        await InsertStepAsync(connection, transaction, id, step, ct).ConfigureAwait(false);
      }

      await transaction.CommitAsync(ct).ConfigureAwait(false);
      return Result.Success(draft with { Id = id, Version = 1 });
    }
    catch (SqliteException ex)
    {
      return Result.Failure<Chain>(Unavailable(ex));
    }
  }

  public async Task<Result<Chain>> GetAsync(int id, CancellationToken ct = default)
  {
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      (ChainRow? row, string? reason) = await ReadChainAsync(connection, id, ct).ConfigureAwait(false);
      if (reason is not null)
      {
        // A single chain has no blast radius to contain, so a corrupt row is a named
        // failure for THIS chain - never an escaping exception.
        return Result.Failure<Chain>(Corrupt(id, reason));
      }

      if (row is null)
      {
        return Result.Failure<Chain>(NotFound(id));
      }

      (List<ChainStep>? steps, string? stepReason) =
          await TryReadStepsAsync(connection, row.Id, ct).ConfigureAwait(false);
      return steps is null
          ? Result.Failure<Chain>(Corrupt(row.Id, stepReason ?? "unreadable step rows"))
          : Result.Success(Materialize(row, steps));
    }
    catch (SqliteException ex)
    {
      return Result.Failure<Chain>(Unavailable(ex));
    }
  }

  public async Task<Result<ChainCatalog>> ListAsync(ChainStatus? status, CancellationToken ct = default)
  {
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      using SqliteCommand command = connection.CreateCommand();
      // Named decision (CA2100): the Replace splices either nothing or a fixed parameterized
      // predicate (@status) - no user input reaches SQL text.
#pragma warning disable CA2100 // Review SQL query for security vulnerabilities
      command.CommandText = "SELECT id, plan_id, plan_version, title, status, park_reason, parked_at_position, session_id, created_at, updated_at, version, workspace_id FROM chains WHERE workspace_id=@ws (@statusFilter) ORDER BY id;"
          .Replace("(@statusFilter)", status is null ? string.Empty : "AND status=@status", StringComparison.Ordinal);
#pragma warning restore CA2100 // Review SQL query for security vulnerabilities
      Add(command, "@ws", _workspaceId);
      if (status is not null)
      {
        Add(command, "@status", status.Value.ToString());
      }

      // One unreadable row must not blank the whole workspace catalog: each chain is
      // materialized in isolation, a corrupt one is skipped and REPORTED. The row's id is
      // read BEFORE the columns that can be corrupt, so a reported chain is identifiable.
      List<Chain> chains = [];
      List<CorruptChainRow> corrupt = [];
      using (SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
      {
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
          int rowId = reader.GetInt32(0);
          if (!TryReadChainRow(reader, out ChainRow? row, out string? chainReason))
          {
            corrupt.Add(new CorruptChainRow(rowId, chainReason ?? "unreadable chain row"));
            continue;
          }

          (List<ChainStep>? steps, string? stepReason) =
              await TryReadStepsAsync(connection, rowId, ct).ConfigureAwait(false);
          if (steps is null)
          {
            corrupt.Add(new CorruptChainRow(rowId, stepReason ?? "unreadable step rows"));
            continue;
          }

          chains.Add(Materialize(row, steps));
        }
      }

      return Result.Success(new ChainCatalog(chains, corrupt));
    }
    catch (SqliteException ex)
    {
      return Result.Failure<ChainCatalog>(Unavailable(ex));
    }
  }

  public async Task<Result<Chain>> SaveAsync(Chain chain, int expectedVersion, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(chain);
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
      await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
#pragma warning restore CA2007

      using (SqliteCommand update = connection.CreateCommand())
      {
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE chains SET title=@title, status=@status, park_reason=@parkReason,
                parked_at_position=@parkedAt, version=version+1, updated_at=@updated
            WHERE id=@id AND workspace_id=@ws AND version=@expected;
            """;
        Add(update, "@title", chain.Title);
        Add(update, "@status", chain.Status.ToString());
        Add(update, "@parkReason", chain.ParkReason);
        Add(update, "@parkedAt", chain.ParkedAtPosition);
        Add(update, "@updated", Format(chain.UpdatedAt));
        Add(update, "@id", chain.Id);
        Add(update, "@ws", _workspaceId);
        Add(update, "@expected", expectedVersion);
        if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
        {
          // The UPDATE's WHERE owned both checks: no matching row means either this
          // workspace cannot see the chain (ChainNotFound) or the version is stale - a real
          // CAS conflict, with the current version read back to name it in the message.
          int? current = await ReadCurrentVersionAsync(connection, transaction, chain.Id, ct).ConfigureAwait(false);
          Result<Chain> miss = current is { } version
              ? Result.Failure<Chain>(new DomainError("VersionConflict", $"chain #{chain.Id} changed under you: expected version {expectedVersion}, current version is {version}."))
              : Result.Failure<Chain>(NotFound(chain.Id));
          await transaction.RollbackAsync(ct).ConfigureAwait(false);
          return miss;
        }
      }

      using (SqliteCommand delete = connection.CreateCommand())
      {
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM chain_steps WHERE chain_id=@chainId;";
        Add(delete, "@chainId", chain.Id);
        _ = await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      }

      foreach (ChainStep step in chain.Steps)
      {
        await InsertStepAsync(connection, transaction, chain.Id, step, ct).ConfigureAwait(false);
      }

      await transaction.CommitAsync(ct).ConfigureAwait(false);
      return Result.Success(chain with { Version = expectedVersion + 1 });
    }
    catch (SqliteException ex)
    {
      return Result.Failure<Chain>(Unavailable(ex));
    }
  }

  /// <summary>The chain claiming a step for this session (issue #301). One indexed query on
  ///     (chain_id, position) joined to the workspace-scoped chains row: a chain in another
  ///     workspace is invisible here for the same reason every other read is.
  ///     <para>A session appears on at most one step by construction (settling clears the
  ///     claim, and a claim is only ever taken by a session the harness just started), so the
  ///     query takes the first match rather than reporting an ambiguity the record cannot
  ///     express.</para></summary>
  public async Task<Result<Chain?>> FindBySessionAsync(string sessionId, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = """
          SELECT c.id FROM chains c
          JOIN chain_steps s ON s.chain_id = c.id
          WHERE c.workspace_id=@ws AND s.session_id=@session
          ORDER BY c.id DESC LIMIT 1;
          """;
      Add(command, "@ws", _workspaceId);
      Add(command, "@session", sessionId);
      object? found = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
      return found is null or DBNull
          ? Result.Success<Chain?>(null)
          : (await GetAsync(Convert.ToInt32(found, CultureInfo.InvariantCulture), ct).ConfigureAwait(false))
              .Map(chain => (Chain?)chain);
    }
    catch (SqliteException ex)
    {
      return Result.Failure<Chain?>(Unavailable(ex));
    }
  }

  private static Chain Materialize(ChainRow row, List<ChainStep> steps)
      => new(row.Id, row.WorkspaceId, row.PlanId, row.PlanVersion, row.Title, row.Status,
          row.ParkReason, row.ParkedAtPosition, row.SessionId, row.CreatedAt, row.UpdatedAt,
          row.Version, steps);

  private static async Task InsertStepAsync(SqliteConnection connection, SqliteTransaction transaction,
      int chainId, ChainStep step, CancellationToken ct)
  {
    using SqliteCommand insert = connection.CreateCommand();
    insert.Transaction = transaction;
    insert.CommandText = """
        INSERT INTO chain_steps (chain_id, position, status, session_id, attempts, outcome, actor,
            claimed_at, completed_at, role, role_model)
        VALUES (@chainId, @position, @status, @session, @attempts, @outcome, @actor, @claimedAt,
            @completedAt, @role, @roleModel);
        """;
    Add(insert, "@chainId", chainId);
    Add(insert, "@position", step.Position);
    Add(insert, "@status", step.Status.ToString());
    Add(insert, "@session", step.SessionId);
    Add(insert, "@attempts", step.Attempts);
    Add(insert, "@outcome", step.Outcome);
    Add(insert, "@actor", step.Actor.Encoded);
    Add(insert, "@claimedAt", step.ClaimedAt is { } claimed ? Format(claimed) : null);
    Add(insert, "@completedAt", step.CompletedAt is { } completed ? Format(completed) : null);
    Add(insert, "@role", step.Role.Encoded);
    Add(insert, "@roleModel", step.Role.Model);
    _ = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
  }

  /// <summary>Reads one chain row. A null row with a null reason means 'not found'; a
  ///     non-null reason means the row EXISTS but cannot be read.</summary>
  private async Task<(ChainRow? Row, string? Reason)> ReadChainAsync(
      SqliteConnection connection, int id, CancellationToken ct)
  {
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT id, plan_id, plan_version, title, status, park_reason, parked_at_position, session_id, created_at, updated_at, version, workspace_id FROM chains WHERE id=@id AND workspace_id=@ws;";
    Add(command, "@id", id);
    Add(command, "@ws", _workspaceId);
    using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
    if (!await reader.ReadAsync(ct).ConfigureAwait(false))
    {
      return (null, null);
    }

    return TryReadChainRow(reader, out ChainRow? row, out string? reason)
        ? (row, null)
        : (null, reason);
  }

  /// <summary>Reads a chain's steps, or reports WHY the chain is unreadable: a null step
  ///     list means the caller should skip this chain and record the reason.</summary>
  private static async Task<(List<ChainStep>? Steps, string? Reason)> TryReadStepsAsync(
      SqliteConnection connection, int chainId, CancellationToken ct)
  {
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT position, status, session_id, attempts, outcome, actor, claimed_at, completed_at, role, role_model FROM chain_steps WHERE chain_id=@chainId ORDER BY position;";
    Add(command, "@chainId", chainId);
    List<ChainStep> steps = [];
    using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
    while (await reader.ReadAsync(ct).ConfigureAwait(false))
    {
      // Stop materializing this chain on the first unreadable column: the caller reports it
      // as a corrupt row and moves on to the next chain.
      (ChainStep? step, string? reason) = await ReadStepAsync(reader, ct).ConfigureAwait(false);
      if (step is null)
      {
        return (null, reason);
      }

      steps.Add(step);
    }

    return (steps, null);
  }

  /// <summary>Reads one step row, or reports WHY it cannot be materialized.
  ///     <para>EXTRACTED FOR COMPLEXITY, and the split is the natural one: the loop owns
  ///     'stop on the first bad row', and this owns 'decode one row'. The three closed-set
  ///     columns (status, actor, role) are each a named refusal rather than a silent default
  ///     - a step whose role nobody can name is worse than a reported row.</para></summary>
  private static async Task<(ChainStep? Step, string? Reason)> ReadStepAsync(
      SqliteDataReader reader, CancellationToken ct)
  {
    string rawStatus = reader.GetString(1);
    if (!TryParseStepStatus(rawStatus, out ChainStepStatus stepStatus))
    {
      return (null, UnknownValueReason("step status", rawStatus));
    }

    string rawActor = reader.GetString(5);
    if (!ChainActor.TryDecode(rawActor, out ChainActor? actor))
    {
      return (null, UnknownValueReason("step actor", rawActor));
    }

    string rawRole = reader.GetString(8);
    string? roleModel = await reader.IsDBNullAsync(9, ct).ConfigureAwait(false) ? null : reader.GetString(9);
    if (!ChainStepRole.TryDecode(rawRole, roleModel, out ChainStepRole? role))
    {
      return (null, UnknownValueReason("step role", rawRole));
    }

    string? sessionId = await reader.IsDBNullAsync(2, ct).ConfigureAwait(false) ? null : reader.GetString(2);
    string? outcome = await reader.IsDBNullAsync(4, ct).ConfigureAwait(false) ? null : reader.GetString(4);
    DateTimeOffset? claimedAt = await reader.IsDBNullAsync(6, ct).ConfigureAwait(false)
        ? null
        : ParseTimestamp(reader.GetString(6));
    DateTimeOffset? completedAt = await reader.IsDBNullAsync(7, ct).ConfigureAwait(false)
        ? null
        : ParseTimestamp(reader.GetString(7));
    return (new ChainStep(reader.GetInt32(0), stepStatus, sessionId, reader.GetInt32(3),
        outcome, actor, claimedAt, completedAt, role), null);
  }

  private async Task<int?> ReadCurrentVersionAsync(SqliteConnection connection, SqliteTransaction transaction,
      int chainId, CancellationToken ct)
  {
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT version FROM chains WHERE id=@id AND workspace_id=@ws;";
    Add(command, "@id", chainId);
    Add(command, "@ws", _workspaceId);
    command.Transaction = transaction;
    using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
    return await reader.ReadAsync(ct).ConfigureAwait(false) ? reader.GetInt32(0) : null;
  }

  private static DomainError Corrupt(int chainId, string reason)
      => new("CorruptChainRow", $"chain #{chainId} could not be read: {reason}");

  private static bool TryReadChainRow(SqliteDataReader reader,
      [NotNullWhen(true)] out ChainRow? row, [NotNullWhen(false)] out string? reason)
  {
    int id = reader.GetInt32(0);
    string rawStatus = reader.GetString(4);
    if (!TryParseStatus(rawStatus, out ChainStatus status))
    {
      row = null;
      reason = UnknownValueReason("status", rawStatus);
      return false;
    }

    row = new ChainRow(
        id,
        reader.GetInt32(1),
        reader.GetInt32(2),
        reader.GetString(3),
        status,
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetInt32(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        ParseTimestamp(reader.GetString(8)),
        ParseTimestamp(reader.GetString(9)),
        reader.GetInt32(10),
        reader.GetString(11));
    reason = null;
    return true;
  }

  private static string UnknownValueReason(string column, string rawValue)
      => $"unknown {column} '{rawValue}' (not a member of its enum)";

  /// <summary>Parses a stored status NAME. Enum.TryParse alone is not enough: it accepts a
  ///     numeric string ('5') and yields an UNDEFINED member, which would smuggle a value
  ///     outside the enum into the domain. A DEFINED member is the only success.</summary>
  private static bool TryParseStatus(string value, out ChainStatus status)
  {
    if (Enum.TryParse(value, ignoreCase: false, out status) && Enum.IsDefined(status))
    {
      return true;
    }

    status = default;
    return false;
  }

  private static bool TryParseStepStatus(string value, out ChainStepStatus status)
  {
    if (Enum.TryParse(value, ignoreCase: false, out status) && Enum.IsDefined(status))
    {
      return true;
    }

    status = default;
    return false;
  }

  private static DateTimeOffset ParseTimestamp(string value)
      => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

  private static string Format(DateTimeOffset value)
      => value.ToString("o", CultureInfo.InvariantCulture);

  private static DomainError NotFound(int id)
      => new("ChainNotFound", $"chain #{id} was not found in this workspace.");

  private static DomainError Unavailable(SqliteException ex)
      => new("StorageUnavailable", $"the chain store could not complete the operation: {ex.Message}");

  private static string ValidateWorkspace(string workspaceId)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
    return workspaceId;
  }

  /// <summary>Internal read model mirroring the chains row before hydration.</summary>
  private sealed record ChainRow(int Id, int PlanId, int PlanVersion, string Title, ChainStatus Status,
      string? ParkReason, int? ParkedAtPosition, string? SessionId, DateTimeOffset CreatedAt,
      DateTimeOffset UpdatedAt, int Version, string WorkspaceId);

  private static void Add(SqliteCommand command, string name, object? value)
      => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
