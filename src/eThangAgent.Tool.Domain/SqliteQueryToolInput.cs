using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed record SqliteQueryToolInput(string Path, string Sql, int MaxRows)
{
  public const int DefaultMaxRows = DbQueryToolInput.DefaultMaxRows;
  public const int MaxRowsLimit = DbQueryToolInput.MaxRowsLimit;

  private const string PathName = "path";
  private const string SqlName = "sql";
  private const string MaxRowsName = "maxRows";
  private const string RequirementText =
      "This tool requires path and sql; maxRows is optional (integer 1..1000, default 100).";

  private static readonly string[] AllowedNames =
      [PathName, SqlName, MaxRowsName, ToolTimeout.ParameterName];

  public static Result<SqliteQueryToolInput> Create(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Fail(baseParse.Error);
    }

    JsonElement json = baseParse.Value;
    DomainError? unknown = ToolArguments.RejectUnknownParameters(json, AllowedNames);
    if (unknown is not null)
    {
      return Fail(unknown);
    }

    Result<string> path = RequireNonEmpty(
        ToolArguments.RequireString(json, PathName, RequirementText),
        "'path' must be a non-empty string.");
    if (!path.IsSuccess)
    {
      return Fail(path.Error);
    }

    Result<string> sql = RequireNonEmpty(
        ToolArguments.RequireString(json, SqlName, RequirementText),
        "'sql' must be a non-empty statement.");
    if (!sql.IsSuccess)
    {
      return Fail(sql.Error);
    }

    Result<int?> maxRows = ToolArguments.OptionalInt(json, MaxRowsName);
    if (!maxRows.IsSuccess)
    {
      return Fail(maxRows.Error);
    }

    Result<int> bounded = maxRows.Value is { } rows && (rows < 1 || rows > MaxRowsLimit)
      ? Result.Failure<int>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'{MaxRowsName}' must be between 1 and {MaxRowsLimit} (got {rows})."))
      : Result.Success(maxRows.Value ?? DefaultMaxRows);
    return !bounded.IsSuccess
      ? Fail(bounded.Error)
      : Result.Success(new SqliteQueryToolInput(path.Value, sql.Value, bounded.Value));
  }

  private static Result<string> RequireNonEmpty(Result<string> text, string emptyMessage) =>
      text.IsSuccess && text.Value.Length == 0
        ? Result.Failure<string>(new DomainError(ToolErrorCodes.InvalidParameterValue, emptyMessage))
        : text;

  private static Result<SqliteQueryToolInput> Fail(DomainError error) =>
      Result.Failure<SqliteQueryToolInput>(error);
}
