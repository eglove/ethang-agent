using System.Globalization;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>Every chain in a workspace with its steps, read for the telemetry report (issue
///     #309).
///     <para>WHY IT DOES NOT GO THROUGH <see cref="SqliteChainStore"/>: that store materializes
///     full AGGREGATES (one steps query per chain, actor parsing, the CAS version) and returns a
///     status-filtered catalog. The report needs every chain's execution columns and nothing
///     else, in one join — so this reads the two tables directly and projects the narrow
///     <see cref="ChainTelemetryChain"/> shape.</para>
///     <para>READ-ONLY: it opens the read-only connection, so no path here can move a chain it
///     is measuring — the <c>IExperimentLedgerAccess</c> precedent, enforced by the connection
///     rather than by discipline.</para>
///     <para>A ROW THAT CANNOT BE MATERIALIZED IS SKIPPED AND REPORTED (the #279 containment):
///     one corrupt chain must not blank a workspace's telemetry, and a report computed over a
///     silently partial set would present itself as complete.</para></summary>
public sealed class SqliteChainTelemetrySource(AppDatabase database, string workspaceId)
    : IChainTelemetrySource
{
  private readonly AppDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
  private readonly string _workspaceId = string.IsNullOrWhiteSpace(workspaceId)
      ? throw new ArgumentException("A workspace id is required.", nameof(workspaceId))
      : workspaceId;

  /// <summary>Chains and their steps in one join, ordered by chain then position so the
  ///     projection is deterministic.
  ///     <para>THE JOIN IS A LEFT JOIN: a chain with no step rows still exists and must be
  ///     reported (a chain created and never advanced is a real fact about the harness), and an
  ///     inner join would silently drop it.</para></summary>
  private const string Sql =
      @"SELECT c.id, c.title, c.status, c.park_reason, c.created_at, c.updated_at,
               s.position, s.status, s.session_id, s.attempts, s.review_attempts, s.role,
               s.role_model, s.claimed_at, s.completed_at
        FROM chains c
        LEFT JOIN chain_steps s ON s.chain_id = c.id
        WHERE c.workspace_id = @ws
        ORDER BY c.id, s.position;";

  public async Task<Result<ChainTelemetryCatalog>> ListAsync(CancellationToken ct = default)
  {
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.OpenReadOnly();
#pragma warning restore CA2007
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = Sql;
      _ = command.Parameters.AddWithValue("@ws", _workspaceId);

      Dictionary<int, ChainAccumulator> chains = [];
      List<int> order = [];
      List<CorruptChainRow> corrupt = [];
      using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
      while (await reader.ReadAsync(ct).ConfigureAwait(false))
      {
        Result<ChainAccumulator> row = ReadRow(reader);
        if (!row.IsSuccess)
        {
          int failedId = reader.GetInt32(0);
          if (!corrupt.Exists(entry => entry.ChainId == failedId))
          {
            corrupt.Add(new CorruptChainRow(failedId, row.Error.Message));
          }

          continue;
        }

        if (!chains.TryGetValue(reader.GetInt32(0), out ChainAccumulator? chain))
        {
          chain = row.Value;
          chains[chain.ChainId] = chain;
          order.Add(chain.ChainId);
        }

        chain.AddStep(reader);
      }

      return Result.Success(new ChainTelemetryCatalog(
          [.. order.Select(id => chains[id].ToChain())], corrupt));
    }
    catch (SqliteException ex)
    {
      return Result.Failure<ChainTelemetryCatalog>(new DomainError("StorageUnavailable",
          $"the chain telemetry read could not read the database: {ex.Message}"));
    }
  }

  /// <summary>Reads one row's chain columns, refusing a row whose status is not a DEFINED enum
  ///     member.
  ///     <para>STRICT ON THE STATUS for the reason the store is: <c>Enum.TryParse</c> accepts a
  ///     numeric string and yields an undefined member, and a report that counted a nonsense
  ///     status as 'not Completed' would quietly change every rate.</para></summary>
  private static Result<ChainAccumulator> ReadRow(SqliteDataReader reader)
  {
    string rawStatus = reader.GetString(2);
    return !Enum.TryParse(rawStatus, ignoreCase: false, out ChainStatus status)
        || !Enum.IsDefined(status)
        ? Result.Failure<ChainAccumulator>(new DomainError("CorruptChainRow",
            $"status '{rawStatus}' is not a chain status."))
        : Build(reader, status);
  }

  /// <summary>The accumulator for a row whose status parsed, reading its clocks and the two
  ///     optional columns.</summary>
  private static Result<ChainAccumulator> Build(SqliteDataReader reader, ChainStatus status)
  {
    (DateTimeOffset created, DateTimeOffset updated) = Timestamps(reader);
    return Result.Success(new ChainAccumulator(reader.GetInt32(0), reader.GetString(1), status,
        reader.IsDBNull(3) ? null : reader.GetString(3), created, updated));
  }

  /// <summary>The chain's two timestamps, parsed tolerantly: a row whose clock cannot be read
  ///     is refused rather than given a default, because a wall-clock metric computed from a
  ///     fabricated start would be a number nobody measured.</summary>
  private static (DateTimeOffset Created, DateTimeOffset Updated) Timestamps(SqliteDataReader reader)
  {
    DateTimeOffset created = Parse(reader.GetString(4));
    return (created, Parse(reader.GetString(5)));
  }

  /// <summary>Parses a stored ISO-8601 timestamp at invariant culture, which is the format the
  ///     store writes.</summary>
  private static DateTimeOffset Parse(string text) => DateTimeOffset.Parse(text,
      CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

  /// <summary>One chain under construction: the chain columns once, then a step per row.
  ///     <para>AN ACCUMULATOR RATHER THAN A LIST OF TUPLES, because a left join gives the chain
  ///     columns on every step row and re-reading them per row would be the same work done
  ///     N times.</para></summary>
  private sealed class ChainAccumulator(int chainId, string title, ChainStatus status,
      string? parkReason, DateTimeOffset createdAt, DateTimeOffset updatedAt)
  {
    private readonly List<ChainTelemetryStep> _steps = [];

    public int ChainId { get; } = chainId;

    /// <summary>Adds the row's step columns. A chain with no step rows (the left join's null
    ///     half) contributes nothing, which is the correct reading of 'no steps yet'.</summary>
    public void AddStep(SqliteDataReader reader)
    {
      if (reader.IsDBNull(6))
      {
        return;
      }

      _steps.Add(new ChainTelemetryStep(reader.GetInt32(6), StepStatus(reader),
          reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetInt32(9), reader.GetInt32(10),
          StepRole(reader), Clock(reader, 13), Clock(reader, 14)));
    }

    /// <summary>The finished chain. Named <c>ToChain</c> rather than <c>Build</c> so it does
    ///     not shadow this file's own <c>Build</c> helper (S3218).</summary>
    public ChainTelemetryChain ToChain() =>
        new(ChainId, title, status, parkReason, createdAt, updatedAt, _steps);

    /// <summary>The step's status, defaulting an unreadable value to Pending.
    ///     <para>DEFAULTED RATHER THAN REFUSED, and that is the safe direction here: the chain
    ///     row already passed its own strict check, so refusing would discard a whole chain over
    ///     one step. A Pending step is not counted as settled, completed, or attempted, so the
    ///     default can only UNDER-report work — never inflate a success rate.</para></summary>
    private static ChainStepStatus StepStatus(SqliteDataReader reader) =>
        Enum.TryParse(reader.GetString(7), ignoreCase: false, out ChainStepStatus status)
        && Enum.IsDefined(status) ? status : ChainStepStatus.Pending;

    /// <summary>The step's role, decoded strictly; an unreadable role reads as Implement.
    ///     <para>IMPLEMENT IS THE RECORD'S OWN DEFAULT (<c>ChainStepRole.Implement</c> is what a
    ///     step with no declared role becomes), so an unreadable value degrades to the same
    ///     answer the domain would have given rather than inventing a third state.</para>
    ///     </summary>
    private static ChainStepRole StepRole(SqliteDataReader reader)
    {
      string? model = reader.IsDBNull(12) ? null : reader.GetString(12);
      return ChainStepRole.TryDecode(reader.GetString(11), model, out ChainStepRole? role)
          ? role
          : ChainStepRole.Implement;
    }

    /// <summary>One nullable clock column, or null when absent.</summary>
    private static DateTimeOffset? Clock(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Parse(reader.GetString(ordinal));
  }
}
