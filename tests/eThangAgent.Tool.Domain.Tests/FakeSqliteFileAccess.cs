using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Fake seam for <see cref="SqliteQueryToolTests"/>: records what the tool
///     asked for and replays a canned outcome.</summary>
internal sealed class FakeSqliteFileAccess : ISqliteFileAccess
{
  public Result<SelfQueryResult>? QueryOutcome { get; init; }
  public string? QueriedPath { get; private set; }
  public string? QueriedSql { get; private set; }
  public int QueriedMaxRows { get; private set; }

  public Task<Result<SelfQueryResult>> QueryAsync(string path, string sql, int maxRows, CancellationToken ct = default)
  {
    QueriedPath = path;
    QueriedSql = sql;
    QueriedMaxRows = maxRows;
    return Task.FromResult(QueryOutcome
        ?? Result.Failure<SelfQueryResult>(new DomainError("Unused", "not exercised")));
  }
}
