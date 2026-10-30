using eThangAgent.AgentDomain;
using eThangAgent.PlanDomain;
using eThangAgent.SharedKernel;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>The plan review record plus each reviewed plan's current version, for the telemetry
///     report (issue #309).
///     <para>WHY IT JOINS THE PLAN: a review is keyed on the plan version it JUDGED (#216 D1), so
///     'did this denial change anything' is only answerable by comparing that version against the
///     plan's current one. Reading the reviews alone would let every denial look like it changed
///     nothing.</para>
///     <para>WORKSPACE ISOLATION IS BY OWNERSHIP, exactly as the review store's is:
///     <c>plan_reviews</c> carries no workspace column, so this joins through <c>plans</c> and a
///     review of another workspace's plan is invisible rather than leaked.</para>
///     <para>READ-ONLY connection, and a verdict outside the enum is SKIPPED rather than
///     counted: an unreadable verdict cannot be classified as a denial or an approval, and
///     guessing one would move the rate it is meant to measure.</para></summary>
public sealed class SqliteChainReviewTelemetrySource(AppDatabase database, string workspaceId)
    : IChainReviewTelemetrySource
{
  private readonly AppDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
  private readonly string _workspaceId = string.IsNullOrWhiteSpace(workspaceId)
      ? throw new ArgumentException("A workspace id is required.", nameof(workspaceId))
      : workspaceId;

  /// <summary>Every review of this workspace's plans, oldest first, with the plan's CURRENT
  ///     version beside it.
  ///     <para>THE JOIN IS THE ISOLATION and the version source at once: one query answers both
  ///     questions, so the set of reviews and the set of versions cannot come from different
  ///     moments.</para></summary>
  private const string Sql =
      @"SELECT r.plan_id, r.plan_version, r.verdict, p.version
        FROM plan_reviews r
        JOIN plans p ON p.id = r.plan_id
        WHERE p.workspace_id = @ws
        ORDER BY r.id;";

  public async Task<Result<ChainTelemetryReviewFacts>> ListAsync(CancellationToken ct = default)
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

      List<ChainTelemetryReview> reviews = [];
      Dictionary<int, int> versions = [];
      using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
      while (await reader.ReadAsync(ct).ConfigureAwait(false))
      {
        int planId = reader.GetInt32(0);
        versions[planId] = reader.GetInt32(3);
        if (Verdict(reader.GetString(2)) is { } verdict)
        {
          reviews.Add(new ChainTelemetryReview(planId, reader.GetInt32(1), verdict));
        }
      }

      return Result.Success(new ChainTelemetryReviewFacts(reviews, versions));
    }
    catch (SqliteException ex)
    {
      return Result.Failure<ChainTelemetryReviewFacts>(new DomainError("StorageUnavailable",
          $"the review telemetry read could not read the record: {ex.Message}"));
    }
  }

  /// <summary>The stored verdict as the CHAIN's spelling, or null when it is not a defined
  ///     member of either enum.
  ///     <para>TWO ENUMS, ONE TOKEN SET: the Plan Domain owns the persisted tokens
  ///     (<c>PlanReviewVerdictCodec</c>: approve / deny / approve-with-conditions) and the Agent
  ///     Domain owns the chain's mirror. The parity between them is pinned by
  ///     <c>ChainReviewVerdictParityTests</c>, so decoding the stored token here cannot invent a
  ///     fourth spelling — an unknown token reads as null and is SKIPPED, never defaulted to
  ///     approve.</para></summary>
  private static ChainReviewVerdict? Verdict(string stored)
  {
    Result<PlanReviewVerdict> decoded = PlanReviewVerdictCodec.Decode(stored);
    return decoded.IsSuccess ? Mirror(decoded.Value) : null;
  }

  /// <summary>The plan verdict as the chain's mirror. Exhaustive, so a new verdict cannot be
  ///     added without deciding what the chain does with it.</summary>
  private static ChainReviewVerdict? Mirror(PlanReviewVerdict plan) => plan switch
  {
    PlanReviewVerdict.Approve => ChainReviewVerdict.Approve,
    PlanReviewVerdict.Deny => ChainReviewVerdict.Deny,
    PlanReviewVerdict.ApproveWithConditions => ChainReviewVerdict.ApproveWithConditions,
    _ => null,
  };
}
