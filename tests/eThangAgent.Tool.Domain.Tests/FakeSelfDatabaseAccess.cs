using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

internal sealed class FakeSelfDatabaseAccess : ISelfDatabaseAccess
{
  public Result<SelfQueryResult>? QueryOutcome { get; init; }
  public Result<SelfDatabaseSchema>? SchemaOutcome { get; init; }
  public string? QueriedSql { get; private set; }
  public int QueriedMaxRows { get; private set; }
  public bool DescribeIncludeCounts { get; private set; }

  public Task<Result<SelfDatabaseSchema>> DescribeAsync(bool includeCounts, CancellationToken ct = default)
  {
    DescribeIncludeCounts = includeCounts;
    return Task.FromResult(SchemaOutcome
        ?? Result.Failure<SelfDatabaseSchema>(new DomainError("Unused", "not exercised")));
  }

  public Task<Result<SelfQueryResult>> QueryAsync(string sql, int maxRows, CancellationToken ct = default)
  {
    QueriedSql = sql;
    QueriedMaxRows = maxRows;
    return Task.FromResult(QueryOutcome
        ?? Result.Failure<SelfQueryResult>(new DomainError("Unused", "not exercised")));
  }
}
