using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using eThangAgent.PlanDomain;
using eThangAgent.SharedKernel;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>SQLite-backed plan persistence: one plans row per plan (global autoincrement
///     id, bound to the workspace given at construction), one plan_steps row per step keyed
///     by (plan_id, position). Every statement filters on the bound workspace, so another
///     workspace's ids are invisible - a Get or Save on one fails PlanNotFound rather than
///     leaking. Expected failures flow as DomainErrors (PlanNotFound, VersionConflict)
///     through Result; storage faults surface as a StorageUnavailable failure, never an
///     exception.</summary>
public sealed class SqlitePlanStore(AppDatabase database, string workspaceId) : IPlanStore
{
  private readonly AppDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
  private readonly string _workspaceId = ValidateWorkspace(workspaceId);

  public async Task<Result<Plan>> CreateAsync(Plan draft, CancellationToken ct = default)
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
            INSERT INTO plans (workspace_id, session_id, title, goal, status, version, created_at, updated_at)
            VALUES (@ws, @session, @title, @goal, @status, 1, @created, @updated);
            """;
        Add(insert, "@ws", _workspaceId);
        Add(insert, "@session", draft.SessionId);
        Add(insert, "@title", draft.Title);
        Add(insert, "@goal", draft.Goal);
        Add(insert, "@status", draft.Status.ToString());
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

      foreach (PlanStep step in draft.Steps)
      {
        await InsertStepAsync(connection, transaction, id, step, ct).ConfigureAwait(false);
      }

      await transaction.CommitAsync(ct).ConfigureAwait(false);
      return Result.Success(draft with { Id = id, Version = 1 });
    }
    catch (SqliteException ex)
    {
      return Result.Failure<Plan>(Unavailable(ex));
    }
  }

  public async Task<Result<Plan>> GetAsync(int id, CancellationToken ct = default)
  {
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      (PlanRow? row, string? reason) = await ReadPlanAsync(connection, id, ct).ConfigureAwait(false);
      if (reason is not null)
      {
        // A single plan has no blast radius to contain, so a corrupt row is a named
        // failure for THIS plan (issue #279) - never an escaping exception.
        return Result.Failure<Plan>(Corrupt(id, reason));
      }

      if (row is null)
      {
        return Result.Failure<Plan>(NotFound(id));
      }

      (List<PlanStep>? steps, string? stepReason) =
          await TryReadStepsAsync(connection, row.Id, ct).ConfigureAwait(false);
      return steps is null
          ? Result.Failure<Plan>(Corrupt(row.Id, stepReason ?? "unreadable step rows"))
          : Result.Success(new Plan(row.Id, row.Title, row.Goal, row.Status, row.SessionId,
              row.CreatedAt, row.UpdatedAt, row.Version, steps));
    }
    catch (SqliteException ex)
    {
      return Result.Failure<Plan>(Unavailable(ex));
    }
  }

  public async Task<Result<PlanCatalog>> ListAsync(PlanStatus? status, CancellationToken ct = default)
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
      command.CommandText = "SELECT id, session_id, title, goal, status, version, created_at, updated_at FROM plans WHERE workspace_id=@ws (@statusFilter) ORDER BY id;"
          .Replace("(@statusFilter)", status is null ? string.Empty : "AND status=@status", StringComparison.Ordinal);
#pragma warning restore CA2100 // Review SQL query for security vulnerabilities
      Add(command, "@ws", _workspaceId);
      if (status is not null)
      {
        Add(command, "@status", status.Value.ToString());
      }

      // Issue #279: one unreadable row must not blank the whole workspace catalog.
      // Each plan is materialized in isolation; a corrupt one is skipped and REPORTED
      // so the caller can render a marker for it while every healthy plan still lists.
      // The row's id is read BEFORE the columns that can be corrupt, so a reported
      // plan is always identifiable.
      List<Plan> plans = [];
      List<CorruptPlanRow> corrupt = [];
      using (SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
      {
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
          int rowId = reader.GetInt32(0);
          if (!TryReadPlanRow(reader, out PlanRow? row, out string? planReason))
          {
            corrupt.Add(new CorruptPlanRow(rowId, planReason ?? "unreadable plan row"));
            continue;
          }

          (List<PlanStep>? steps, string? stepReason) =
              await TryReadStepsAsync(connection, rowId, ct).ConfigureAwait(false);
          if (steps is null)
          {
            corrupt.Add(new CorruptPlanRow(rowId, stepReason ?? "unreadable step rows"));
            continue;
          }

          plans.Add(new Plan(row.Id, row.Title, row.Goal, row.Status, row.SessionId,
              row.CreatedAt, row.UpdatedAt, row.Version, steps));
        }
      }

      return Result.Success(new PlanCatalog(plans, corrupt));
    }
    catch (SqliteException ex)
    {
      return Result.Failure<PlanCatalog>(Unavailable(ex));
    }
  }

  public async Task<Result<Plan>> SaveAsync(Plan plan, int expectedVersion, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(plan);
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
            UPDATE plans SET title=@title, goal=@goal, status=@status, version=version+1, updated_at=@updated
            WHERE id=@id AND workspace_id=@ws AND version=@expected;
            """;
        Add(update, "@title", plan.Title);
        Add(update, "@goal", plan.Goal);
        Add(update, "@status", plan.Status.ToString());
        Add(update, "@updated", Format(plan.UpdatedAt));
        Add(update, "@id", plan.Id);
        Add(update, "@ws", _workspaceId);
        Add(update, "@expected", expectedVersion);
        if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
        {
          // The UPDATE's WHERE owned both checks: no matching row means either this
          // workspace cannot see the plan (PlanNotFound) or the version is stale - a real
          // CAS conflict, with the current version read back to name it in the message.
          int? current = await ReadCurrentVersionAsync(connection, transaction, plan.Id, ct).ConfigureAwait(false);
          Result<Plan> miss = current is { } version
              ? Result.Failure<Plan>(new DomainError("VersionConflict", $"plan #{plan.Id} changed under you: expected version {expectedVersion}, current version is {version}."))
              : Result.Failure<Plan>(NotFound(plan.Id));
          await transaction.RollbackAsync(ct).ConfigureAwait(false);
          return miss;
        }
      }

      using (SqliteCommand delete = connection.CreateCommand())
      {
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM plan_steps WHERE plan_id=@planId;";
        Add(delete, "@planId", plan.Id);
        _ = await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      }

      foreach (PlanStep step in plan.Steps)
      {
        await InsertStepAsync(connection, transaction, plan.Id, step, ct).ConfigureAwait(false);
      }

      await transaction.CommitAsync(ct).ConfigureAwait(false);
      return Result.Success(plan with { Version = expectedVersion + 1 });
    }
    catch (SqliteException ex)
    {
      return Result.Failure<Plan>(Unavailable(ex));
    }
  }

  private static async Task InsertStepAsync(SqliteConnection connection, SqliteTransaction transaction,
      int planId, PlanStep step, CancellationToken ct)
  {
    using SqliteCommand insert = connection.CreateCommand();
    insert.Transaction = transaction;
    insert.CommandText = """
        INSERT INTO plan_steps (plan_id, position, title, detail, todo_id, status)
        VALUES (@planId, @position, @title, @detail, @todoId, @status);
        """;
    Add(insert, "@planId", planId);
    Add(insert, "@position", step.Position);
    Add(insert, "@title", step.Title);
    Add(insert, "@detail", step.Detail);
    Add(insert, "@todoId", step.TodoId);
    Add(insert, "@status", step.Status.ToString());
    _ = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
  }

  /// <summary>Reads one plan row. A null row with a null reason means 'not found'; a
  ///     non-null reason means the row EXISTS but cannot be read (issue #279).</summary>
  private async Task<(PlanRow? Row, string? Reason)> ReadPlanAsync(
      SqliteConnection connection, int id, CancellationToken ct)
  {
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT id, session_id, title, goal, status, version, created_at, updated_at FROM plans WHERE id=@id AND workspace_id=@ws;";
    Add(command, "@id", id);
    Add(command, "@ws", _workspaceId);
    using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
    if (!await reader.ReadAsync(ct).ConfigureAwait(false))
    {
      return (null, null);
    }

    return TryReadPlanRow(reader, out PlanRow? row, out string? reason)
        ? (row, null)
        : (null, reason);
  }

  /// <summary>Reads a plan's steps, or reports WHY the plan is unreadable (issue #279):
  ///     a null step list means the caller should skip this plan and record the reason.</summary>
  private static async Task<(List<PlanStep>? Steps, string? Reason)> TryReadStepsAsync(
      SqliteConnection connection, int planId, CancellationToken ct)
  {
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT position, title, detail, todo_id, status FROM plan_steps WHERE plan_id=@planId ORDER BY position;";
    Add(command, "@planId", planId);
    List<PlanStep> steps = [];
    using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
    while (await reader.ReadAsync(ct).ConfigureAwait(false))
    {
      int? todoId = await reader.IsDBNullAsync(3, ct).ConfigureAwait(false) ? null : reader.GetInt32(3);
      string? detail = await reader.IsDBNullAsync(2, ct).ConfigureAwait(false) ? null : reader.GetString(2);
      string rawStatus = reader.GetString(4);
      if (!TryParseStepStatus(rawStatus, out PlanStepStatus stepStatus))
      {
        // Stop materializing this plan: the caller reports it as a corrupt row and
        // moves on to the next plan (issue #279).
        return (null, UnknownStatusReason("step status", rawStatus));
      }

      steps.Add(new PlanStep(
          reader.GetInt32(0),
          reader.GetString(1),
          detail,
          todoId,
          stepStatus));
    }

    return (steps, null);
  }

  private async Task<int?> ReadCurrentVersionAsync(SqliteConnection connection, SqliteTransaction transaction,
      int planId, CancellationToken ct)
  {
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT version FROM plans WHERE id=@id AND workspace_id=@ws;";
    Add(command, "@id", planId);
    Add(command, "@ws", _workspaceId);
    command.Transaction = transaction;
    using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
    return await reader.ReadAsync(ct).ConfigureAwait(false) ? reader.GetInt32(0) : null;
  }

  private static DomainError Corrupt(int planId, string reason)
      => new("CorruptPlanRow", $"plan #{planId} could not be read: {reason}");

  private static bool TryReadPlanRow(SqliteDataReader reader,
      [NotNullWhen(true)] out PlanRow? row, [NotNullWhen(false)] out string? reason)
  {
    int id = reader.GetInt32(0);
    string rawStatus = reader.GetString(4);
    if (!TryParseStatus(rawStatus, out PlanStatus status))
    {
      row = null;
      reason = UnknownStatusReason("status", rawStatus);
      return false;
    }

    row = new PlanRow(
        id,
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        status,
        reader.GetInt32(5),
        ParseTimestamp(reader.GetString(6)),
        ParseTimestamp(reader.GetString(7)));
    reason = null;
    return true;
  }

  private static string UnknownStatusReason(string column, string rawValue)
      => $"unknown {column} '{rawValue}' (not a member of its status enum)";

  /// <summary>Parses a stored status NAME. Enum.TryParse alone is not enough: it accepts a
  ///     numeric string ('5') and yields an UNDEFINED member, which would smuggle a value
  ///     outside the enum into the domain. A DEFINED member is the only success.</summary>
  private static bool TryParseStatus(string value, out PlanStatus status)
  {
    if (Enum.TryParse(value, ignoreCase: false, out status) && Enum.IsDefined(status))
    {
      return true;
    }

    status = default;
    return false;
  }

  private static bool TryParseStepStatus(string value, out PlanStepStatus status)
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
      => new("PlanNotFound", $"plan #{id} was not found in this workspace.");

  private static DomainError Unavailable(SqliteException ex)
      => new("StorageUnavailable", $"the plan store could not complete the operation: {ex.Message}");

  private static string ValidateWorkspace(string workspaceId)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
    return workspaceId;
  }

  /// <summary>Internal read model mirroring the plans row before hydration.</summary>
  private sealed record PlanRow(int Id, string SessionId, string Title, string Goal, PlanStatus Status, int Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

  private static void Add(SqliteCommand command, string name, object? value)
      => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
