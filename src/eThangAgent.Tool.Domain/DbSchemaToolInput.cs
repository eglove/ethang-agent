using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed record DbSchemaToolInput(bool IncludeCounts)
{
  private const string IncludeCountsName = "includeCounts";

  private static readonly string[] AllowedNames =
      [IncludeCountsName, ToolTimeout.ParameterName];

  public static Result<DbSchemaToolInput> Create(string jsonArguments)
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

    Result<bool?> includeCounts = ToolArguments.OptionalBool(json, IncludeCountsName);
    return !includeCounts.IsSuccess
      ? Fail(includeCounts.Error)
      : Result.Success(new DbSchemaToolInput(includeCounts.Value ?? false));
  }

  private static Result<DbSchemaToolInput> Fail(DomainError err) =>
      Result.Failure<DbSchemaToolInput>(err);
}
