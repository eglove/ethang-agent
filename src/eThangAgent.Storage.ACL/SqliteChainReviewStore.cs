using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>SQLite-backed implementation review persistence: one <c>chain_reviews</c> row per
///     verdict, bound to the workspace given at construction.
///     <para>THIS IS NOT <see cref="SqlitePlanReviewStore"/>. That stores judgments of a PLAN
///     VERSION, before anything is built; this stores judgments of WORK THAT WAS BUILT. One table
///     for both is what made the two indistinguishable, so they are separate tables with separate
///     verdict vocabularies and separate readers.</para>
///     <para>WORKSPACE ISOLATION IS BY OWNERSHIP, NOT BY A COLUMN: a review is a child of a
///     chain, and a chain carries the workspace, so every statement joins through <c>chains</c>
///     and another workspace's review is invisible.</para>
///     <para>APPEND-ONLY: no update and no delete. A verdict is a historical fact.</para>
///     <para>Expected failures flow as DomainErrors through Result; nothing here throws for an
///     expected failure.</para></summary>
public sealed class SqliteChainReviewStore(AppDatabase database, string workspaceId)
    : IChainReviewSource
{
  private readonly AppDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
  private readonly string _workspaceId = ValidateWorkspace(workspaceId);

  /// <inheritdoc />
  public async Task<Result<ChainReviewRecord>> RecordAsync(int chainId, int judgedPosition,
      ChainReviewRecord review, string reviewerSessionId, DateTimeOffset now,
      CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(review);
    ArgumentException.ThrowIfNullOrWhiteSpace(reviewerSessionId);
    if (judgedPosition < 1)
    {
      return Result.Failure<ChainReviewRecord>(new DomainError("InvalidReviewTarget",
          $"a review judges a step at position 1 or later; {judgedPosition} is not a position."));
    }

    // THE WRITE-TIME GATE: a finding with no citation is a complaint, not a finding, and the
    // refusal names the index so a caller can fix exactly that one.
    for (int i = 0; i < review.Findings.Count; i++)
    {
      if (!review.Findings[i].IsWellFormed)
      {
        return Result.Failure<ChainReviewRecord>(new DomainError("UncitedFinding",
            $"review finding {i} has no text or no citation; every finding must cite what it is about."));
      }
    }

    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      if (!await ChainExistsAsync(connection, chainId, ct).ConfigureAwait(false))
      {
        return Result.Failure<ChainReviewRecord>(NotFound(chainId));
      }

      using SqliteCommand insert = connection.CreateCommand();
      insert.CommandText = """
          INSERT INTO chain_reviews (chain_id, judged_position, reviewer_session_id, verdict,
              findings_json, created_at)
          VALUES (@chainId, @judged, @session, @verdict, @findings, @created);
          """;
      Add(insert, "@chainId", chainId);
      Add(insert, "@judged", judgedPosition);
      Add(insert, "@session", reviewerSessionId.Trim());
      Add(insert, "@verdict", review.Verdict.ToString());
      Add(insert, "@findings", SerializeFindings(review.Findings));
      Add(insert, "@created", Format(now));
      _ = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      return Result.Success(review);
    }
    catch (SqliteException ex)
    {
      return Result.Failure<ChainReviewRecord>(Unavailable(ex));
    }
  }

  /// <inheritdoc />
  public async Task<Result<ChainReviewRecord?>> FindForSessionAsync(int chainId,
      string reviewerSessionId, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(reviewerSessionId);
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      string sql = """
          SELECT r.verdict, r.findings_json
          FROM chain_reviews r
          JOIN chains c ON c.id = r.chain_id
          WHERE r.chain_id = @chainId AND r.reviewer_session_id = @session
              AND c.workspace_id = @ws
          ORDER BY r.id DESC
          LIMIT 1;
          """;
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = sql;
      Add(command, "@chainId", chainId);
      Add(command, "@session", reviewerSessionId.Trim());
      Add(command, "@ws", _workspaceId);
      using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
      if (!await reader.ReadAsync(ct).ConfigureAwait(false))
      {
        return Result.Success<ChainReviewRecord?>(null);
      }

      string rawVerdict = reader.GetString(0);
      if (!TryParseVerdict(rawVerdict, out ChainReviewVerdict verdict))
      {
        return Result.Failure<ChainReviewRecord?>(
            new DomainError("CorruptReviewRow",
                $"chain #{chainId} has a review whose verdict '{rawVerdict}' is not Pass or Fail."));
      }

      Result<IReadOnlyList<ChainReviewFinding>> findings =
          DeserializeFindings(reader.GetString(1));
      return findings.IsSuccess
          ? Result.Success<ChainReviewRecord?>(
              new ChainReviewRecord(verdict, findings.Value))
          : Result.Failure<ChainReviewRecord?>(findings.Error);
    }
    catch (SqliteException ex)
    {
      return Result.Failure<ChainReviewRecord?>(Unavailable(ex));
    }
  }

  private async Task<bool> ChainExistsAsync(SqliteConnection connection, int chainId,
      CancellationToken ct)
  {
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM chains WHERE id=@id AND workspace_id=@ws;";
    Add(command, "@id", chainId);
    Add(command, "@ws", _workspaceId);
    return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false),
        CultureInfo.InvariantCulture) > 0;
  }

  private static bool TryParseVerdict(string raw,
      [NotNullWhen(true)] out ChainReviewVerdict verdict)
      => Enum.TryParse(raw, ignoreCase: false, out verdict) && Enum.IsDefined(verdict);

  private static string SerializeFindings(IReadOnlyList<ChainReviewFinding> findings)
      => JsonSerializer.Serialize(
          findings.Select(finding => new FindingRow(finding.Text, finding.Citation)).ToList());

  /// <summary>The findings a stored row carries, or a named failure when the JSON is corrupt:
  ///     a review whose findings cannot be read is a verdict with nothing behind it.</summary>
  /// <param name="json">The stored findings JSON.</param>
  private static Result<IReadOnlyList<ChainReviewFinding>> DeserializeFindings(string json)
  {
    try
    {
      List<FindingRow>? rows = JsonSerializer.Deserialize<List<FindingRow>>(json);
      return Result.Success<IReadOnlyList<ChainReviewFinding>>(
          rows is null
              ? []
              : [.. rows.Select(row => new ChainReviewFinding(row.Text, row.Citation))]);
    }
    catch (JsonException)
    {
      return Result.Failure<IReadOnlyList<ChainReviewFinding>>(new DomainError("CorruptReviewRow",
          "a stored review's findings could not be read."));
    }
  }

  /// <summary>A finding as it is stored. A private shape so the JSON does not become a contract
  ///     the domain record has to keep.</summary>
  /// <param name="Text">What the reviewer observed.</param>
  /// <param name="Citation">What it is about.</param>
  private sealed record FindingRow(string Text, string Citation);

  private static DomainError NotFound(int chainId) => new("ChainNotFound",
      $"chain #{chainId} does not exist in this workspace, so its review has nowhere to go.");

  private static DomainError Unavailable(SqliteException ex) => new("StorageUnavailable",
      $"the chain review store is unavailable: {ex.Message}");

  private static string Format(DateTimeOffset value)
      => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

  private static string ValidateWorkspace(string workspaceId) => WorkspaceIdentity.Require(workspaceId);

  private static void Add(SqliteCommand command, string name, object? value)
      => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
