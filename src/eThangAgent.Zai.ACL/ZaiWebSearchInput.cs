using System.Text.Json;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Zai.ACL;

public sealed record ZaiWebSearchInput(string Query, int Count, string? Recency)
{
  internal const string AllowedList = "query, count, recency, timeoutSeconds";

  private const string QueryName = "query";
  private const string CountName = "count";
  private const string RecencyName = "recency";
  private const int DefaultCount = 10;

  public static Result<ZaiWebSearchInput> Create(JsonElement json)
  {
    DomainError? unknown = ZaiToolInput.RejectUnknown(json, AllowedList, QueryName, CountName, RecencyName);
    if (unknown is not null)
    {
      return Fail(unknown);
    }

    Result<string> query = ParseQuery(json);
    if (!query.IsSuccess)
    {
      return Fail(query.Error);
    }

    Result<int> count = ParseCount(json);
    if (!count.IsSuccess)
    {
      return Fail(count.Error);
    }

    Result<string?> recency = ParseRecency(json);
    if (!recency.IsSuccess)
    {
      return Fail(recency.Error);
    }

    ZaiWebSearchInput input = new(query.Value, count.Value, recency.Value);
    return Result.Success(input);
  }

  private static Result<string> ParseQuery(JsonElement json)
  {
    if (!json.TryGetProperty(QueryName, out JsonElement queryEl))
    {
      return Result.Failure<string>(new DomainError(ToolErrorCodes.MissingParameter,
          $"Missing required parameter '{QueryName}'. This tool requires query."));
    }

    if (queryEl.ValueKind != JsonValueKind.String)
    {
      return Result.Failure<string>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'{QueryName}' must be a string, but got {queryEl.ValueKind}."));
    }

    Result<string> nonEmpty = queryEl.GetString()!.Length > 0
      ? Result.Success(queryEl.GetString()!)
      : Result.Failure<string>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "'query' must be a non-empty string."));
    return nonEmpty;
  }

  private static Result<int> ParseCount(JsonElement json)
  {
    if (!json.TryGetProperty(CountName, out JsonElement countEl))
    {
      return Result.Success(DefaultCount);
    }

    if (countEl.ValueKind != JsonValueKind.Number || !countEl.TryGetInt32(out int count))
    {
      return Result.Failure<int>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'{CountName}' must be a integer, but got {countEl.ValueKind}."));
    }

    Result<int> inRange = count is >= 1 and <= 50
      ? Result.Success(count)
      : Result.Failure<int>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'{CountName}' must be 1..50 (got {count})."));
    return inRange;
  }

  private static Result<string?> ParseRecency(JsonElement json)
  {
    if (!json.TryGetProperty(RecencyName, out JsonElement recencyEl))
    {
      return Result.Success<string?>(null);
    }

    if (recencyEl.ValueKind != JsonValueKind.String)
    {
      return Result.Failure<string?>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'{RecencyName}' must be a string, but got {recencyEl.ValueKind}."));
    }

    string recency = recencyEl.GetString()!;
    Result<string?> allowed = recency is "oneDay" or "oneWeek" or "oneMonth" or "oneYear" or "noLimit"
      ? Result.Success<string?>(recency)
      : Result.Failure<string?>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'{RecencyName}' must be exactly one of oneDay, oneWeek, oneMonth, oneYear, noLimit (got \"{recency}\")."));
    return allowed;
  }

  private static Result<ZaiWebSearchInput> Fail(DomainError err) =>
      Result.Failure<ZaiWebSearchInput>(err);
}
