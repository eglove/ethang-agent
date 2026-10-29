using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using eThangAgent.PlanDomain;
using eThangAgent.SharedKernel;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>SQLite-backed plan review persistence (issue #216): one <c>plan_reviews</c> row
///     per review, bound to the workspace given at construction.
///     <para>WORKSPACE ISOLATION IS BY OWNERSHIP, NOT BY A COLUMN. The table carries no
///     <c>workspace_id</c>: a review is a child of a plan, and the plan already carries the
///     workspace, so every statement joins through <c>plans</c> and a review of another
///     workspace's plan is invisible - a Get or Create on one fails <c>PlanNotFound</c>
///     rather than leaking. A duplicated column would be a second copy of one fact that
///     could disagree with the first.</para>
///     <para>Expected failures flow as DomainErrors (PlanNotFound, UnknownVerdict,
///     CorruptReviewRow, StorageUnavailable) through Result; nothing here throws for an
///     expected failure.</para></summary>
public sealed class SqlitePlanReviewStore(AppDatabase database, string workspaceId) : IPlanReviewStore
{
  private readonly AppDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
  private readonly string _workspaceId = ValidateWorkspace(workspaceId);

  public async Task<Result<PlanReview>> CreateAsync(PlanReview review, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(review);
    // The write-time gate: a record a caller assembled by hand is checked exactly as one built
    // through the factory is, and a refusal never reaches the database.
    Result<PlanReview> valid = review.Validate();
    if (!valid.IsSuccess)
    {
      return Result.Failure<PlanReview>(valid.Error);
    }

    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
      await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
#pragma warning restore CA2007

      if (!await PlanExistsAsync(connection, transaction, review.PlanId, ct).ConfigureAwait(false))
      {
        await transaction.RollbackAsync(ct).ConfigureAwait(false);
        return Result.Failure<PlanReview>(NotFound(review.PlanId));
      }

      using (SqliteCommand insert = connection.CreateCommand())
      {
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO plan_reviews (plan_id, plan_version, verdict, reviewer_session_id,
                reviewer_model, requirement_coverage, testability, blast_radius, findings_json,
                notes, created_at)
            VALUES (@planId, @planVersion, @verdict, @session, @model, @coverage, @testability,
                @blast, @findings, @notes, @created);
            """;
        Add(insert, "@planId", review.PlanId);
        Add(insert, "@planVersion", review.PlanVersion);
        Add(insert, "@verdict", PlanReviewVerdictCodec.Encode(review.Verdict));
        Add(insert, "@session", review.ReviewerSessionId);
        Add(insert, "@model", review.ReviewerModel);
        Add(insert, "@coverage", review.Scores.RequirementCoverage);
        Add(insert, "@testability", review.Scores.Testability);
        Add(insert, "@blast", review.Scores.BlastRadius);
        Add(insert, "@findings", SerializeFindings(review.Findings));
        Add(insert, "@notes", review.Notes);
        Add(insert, "@created", Format(review.CreatedAt));
        _ = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      }

      int id;
      using (SqliteCommand idCommand = connection.CreateCommand())
      {
        idCommand.Transaction = transaction;
        idCommand.CommandText = "SELECT last_insert_rowid();";
        id = Convert.ToInt32(await idCommand.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
      }

      await transaction.CommitAsync(ct).ConfigureAwait(false);
      return Result.Success(valid.Value with { Id = id });
    }
    catch (SqliteException ex)
    {
      return Result.Failure<PlanReview>(Unavailable(ex));
    }
  }

  public async Task<Result<IReadOnlyList<PlanReview>>> ListForPlanAsync(int planId, CancellationToken ct = default)
  {
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      List<PlanReview> reviews = [];
      string? corrupt = await ReadReviewsAsync(connection, planId, reviews, ct).ConfigureAwait(false);
      return corrupt is null
          ? Result.Success<IReadOnlyList<PlanReview>>(reviews)
          : Result.Failure<IReadOnlyList<PlanReview>>(Corrupt(planId, corrupt));
    }
    catch (SqliteException ex)
    {
      return Result.Failure<IReadOnlyList<PlanReview>>(Unavailable(ex));
    }
  }

  public async Task<Result<PlanReview?>> LatestForPlanAsync(int planId, CancellationToken ct = default)
  {
    Result<IReadOnlyList<PlanReview>> listed = await ListForPlanAsync(planId, ct).ConfigureAwait(false);
    return listed.IsSuccess
        ? Result.Success(LastOrNull(listed.Value))
        : Result.Failure<PlanReview?>(listed.Error);
  }

  /// <summary>The newest review in a creation-ordered list, or null when there is none.
  ///     Extracted so the caller is one conditional rather than two (S3358).</summary>
  private static PlanReview? LastOrNull(IReadOnlyList<PlanReview> reviews) =>
      reviews.Count == 0 ? null : reviews[^1];

  /// <summary>Reads a plan's reviews in creation order, appending to
  ///     <paramref name="into"/>. A row that cannot be materialized is reported by its
  ///     reason rather than silently skipped: a review the caller cannot see is a verdict
  ///     the cursor would then guess about, and guessing is what the record exists to end.
  ///     <para>The reason is RETURNED rather than thrown so the caller decides, exactly as
  ///     <c>SqlitePlanStore</c> reports a corrupt plan row.</para></summary>
  private async Task<string?> ReadReviewsAsync(SqliteConnection connection, int planId,
      List<PlanReview> into, CancellationToken ct)
  {
    using SqliteCommand command = connection.CreateCommand();
    // The join is what enforces workspace isolation: a review is visible only through a
    // plan this workspace owns, because the table carries no workspace column of its own.
    command.CommandText = """
        SELECT r.id, r.plan_id, r.plan_version, r.verdict, r.reviewer_session_id, r.reviewer_model,
            r.requirement_coverage, r.testability, r.blast_radius, r.findings_json, r.notes, r.created_at
        FROM plan_reviews r
        JOIN plans p ON p.id = r.plan_id
        WHERE r.plan_id = @planId AND p.workspace_id = @ws
        ORDER BY r.id;
        """;
    Add(command, "@planId", planId);
    Add(command, "@ws", _workspaceId);
    using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
    while (await reader.ReadAsync(ct).ConfigureAwait(false))
    {
      if (!TryReadReview(reader, out PlanReview? review, out string? reason))
      {
        return reason;
      }

      into.Add(review);
    }

    return null;
  }

  private static bool TryReadReview(SqliteDataReader reader,
      [NotNullWhen(true)] out PlanReview? review, [NotNullWhen(false)] out string? reason)
  {
    int id = reader.GetInt32(0);
    string rawVerdict = reader.GetString(3);
    Result<PlanReviewVerdict> decoded = PlanReviewVerdictCodec.Decode(rawVerdict);
    if (!decoded.IsSuccess)
    {
      review = null;
      reason = $"review #{id}: {decoded.Error.Message}";
      return false;
    }

    if (!TryReadFindings(reader.GetString(9), out List<PlanReviewFinding>? findings, out string? findingReason))
    {
      review = null;
      reason = $"review #{id}: {findingReason}";
      return false;
    }

    review = new PlanReview(
        id,
        reader.GetInt32(1),
        reader.GetInt32(2),
        decoded.Value,
        reader.GetString(4),
        reader.GetString(5),
        new PlanReviewScores(reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8)),
        findings,
        reader.IsDBNull(10) ? null : reader.GetString(10),
        ParseTimestamp(reader.GetString(11)));
    reason = null;
    return true;
  }

  /// <summary>Reads the findings array. A payload that is not a JSON array of finding
  ///     objects is a NAMED reason, never an empty list: an unreadable findings array is
  ///     exactly the case where a fix session would be told 'the reviewer denied' with no
  ///     notes, which is the failure #219 exists to prevent.</summary>
  private static bool TryReadFindings(string payload,
      [NotNullWhen(true)] out List<PlanReviewFinding>? findings, [NotNullWhen(false)] out string? reason)
  {
    findings = null;
    try
    {
      List<PlanReviewFinding>? parsed = JsonSerializer.Deserialize<List<PlanReviewFinding>>(payload);
      if (parsed is null)
      {
        reason = "the findings payload deserialized to null.";
        return false;
      }

      findings = parsed;
      reason = null;
      return true;
    }
    catch (JsonException ex)
    {
      reason = $"the findings payload is not readable JSON: {ex.Message}";
      return false;
    }
  }

  private static string SerializeFindings(IReadOnlyList<PlanReviewFinding> findings) =>
      JsonSerializer.Serialize(findings);

  private async Task<bool> PlanExistsAsync(SqliteConnection connection,
      SqliteTransaction transaction, int planId, CancellationToken ct)
  {
    using SqliteCommand command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = "SELECT 1 FROM plans WHERE id=@id AND workspace_id=@ws;";
    Add(command, "@id", planId);
    Add(command, "@ws", _workspaceId);
    return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
  }

  private static DateTimeOffset ParseTimestamp(string value)
      => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

  private static string Format(DateTimeOffset value)
      => value.ToString("o", CultureInfo.InvariantCulture);

  private static DomainError NotFound(int planId)
      => new("PlanNotFound", $"plan #{planId} was not found in this workspace, so it cannot be reviewed.");

  private static DomainError Corrupt(int planId, string reason)
      => new("CorruptReviewRow", $"a review of plan #{planId} could not be read: {reason}");

  private static DomainError Unavailable(SqliteException ex)
      => new("StorageUnavailable", $"the plan review store could not complete the operation: {ex.Message}");

  private static string ValidateWorkspace(string workspaceId)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
    return workspaceId;
  }

  private static void Add(SqliteCommand command, string name, object? value)
      => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
