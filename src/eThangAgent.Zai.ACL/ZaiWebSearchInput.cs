using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.Zai.ACL;

public sealed record ZaiWebSearchInput(string Query, int Count, string? Recency)
{
  internal const string AllowedList = "query, count, recency, timeoutSeconds";

  public static Result<ZaiWebSearchInput> Create(JsonElement json)
  {
    DomainError? unknown = ZaiToolInput.RejectUnknown(json, AllowedList, "query", "count", "recency");
    if (unknown is not null)
    {
      return Fail(unknown);
    }

    if (!json.TryGetProperty("query", out JsonElement queryEl))
    {
      return Missing("query");
    }
    if (queryEl.ValueKind != JsonValueKind.String)
    {
      return WrongType("query", "string", queryEl.ValueKind);
    }
    string query = queryEl.GetString()!;
    if (query.Length == 0)
    {
      return Fail(new DomainError("InvalidParameterValue", "'query' must be a non-empty string."));
    }

    int count = 10;
    if (json.TryGetProperty("count", out JsonElement countEl))
    {
      if (countEl.ValueKind != JsonValueKind.Number || !countEl.TryGetInt32(out count))
      {
        return WrongType("count", "integer", countEl.ValueKind);
      }
      if (count is < 1 or > 50)
      {
        return Fail(new DomainError("InvalidParameterValue", $"'count' must be 1..50 (got {count})."));
      }
    }

    string? recency = null;
    if (json.TryGetProperty("recency", out JsonElement recencyEl))
    {
      if (recencyEl.ValueKind != JsonValueKind.String)
      {
        return WrongType("recency", "string", recencyEl.ValueKind);
      }
      recency = recencyEl.GetString();
      if (recency is not ("oneDay" or "oneWeek" or "oneMonth" or "oneYear" or "noLimit"))
      {
        return Fail(new DomainError("InvalidParameterValue",
            $"'recency' must be exactly one of oneDay, oneWeek, oneMonth, oneYear, noLimit (got \"{recency}\")."));
      }
    }

    return Result.Success(new ZaiWebSearchInput(query, count, recency));
  }

  private static Result<ZaiWebSearchInput> Missing(string n) =>
      Result.Failure<ZaiWebSearchInput>(new DomainError("MissingParameter",
          $"Missing required parameter '{n}'. This tool requires query."));

  private static Result<ZaiWebSearchInput> WrongType(string n, string e, JsonValueKind a) =>
      Result.Failure<ZaiWebSearchInput>(new DomainError("InvalidParameterType",
          $"'{n}' must be a {e}, but got {a}."));

  private static Result<ZaiWebSearchInput> Fail(DomainError err) =>
      Result.Failure<ZaiWebSearchInput>(err);
}
