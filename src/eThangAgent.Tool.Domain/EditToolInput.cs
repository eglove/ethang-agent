using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed record EditToolInput(string Path, string Old, string New, bool All, int Occurrences)
{
  private const string PathName = "path";
  private const string OldName = "old";
  private const string NewName = "new";
  private const string AllName = "all";
  private const string OccurrencesName = "occurrences";
  private const string RequirementText =
      "This tool requires path, old, and new, plus exactly one of 'all' or 'occurrences'.";

  private static readonly string[] AllowedNames =
      [PathName, OldName, NewName, AllName, OccurrencesName, ToolTimeout.ParameterName];

  public static Result<EditToolInput> Create(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Fail(baseParse.Error!);
    }

    JsonElement json = baseParse.Value;
    DomainError? unknown = ToolArguments.RejectUnknownParameters(json, AllowedNames);
    if (unknown is not null)
    {
      return Fail(unknown);
    }

    Result<string> path = RequireNonEmptyText(
        ToolArguments.RequireString(json, PathName, RequirementText),
        "'path' must be a non-empty string.");
    if (!path.IsSuccess)
    {
      return Fail(path.Error!);
    }

    Result<string> old = RequireNonEmptyText(
        ToolArguments.RequireString(json, OldName, RequirementText),
        "'old' must be a non-empty string — an empty anchor would match everywhere.");
    if (!old.IsSuccess)
    {
      return Fail(old.Error!);
    }

    // 'new' may be empty: deletion is explicit intent.
    Result<string> @new = ToolArguments.RequireString(json, NewName, RequirementText);
    if (!@new.IsSuccess)
    {
      return Fail(@new.Error!);
    }

    Result<(bool All, int Occurrences)> selector = ParseSelector(json);
    if (!selector.IsSuccess)
    {
      return Fail(selector.Error!);
    }

    EditToolInput input = new(path.Value!, old.Value!, @new.Value!, selector.Value.All, selector.Value.Occurrences);
    return Result.Success(input);
  }

  /// <summary>Rejects an empty string after the type check passes.</summary>
  private static Result<string> RequireNonEmptyText(Result<string> text, string emptyMessage)
  {
    if (!text.IsSuccess)
    {
      return text;
    }

    Result<string> nonEmpty = text.Value!.Length > 0
      ? text
      : Result.Failure<string>(new DomainError(ToolErrorCodes.InvalidParameterValue, emptyMessage));
    return nonEmpty;
  }

  /// <summary>Exactly one of 'all' (boolean true) or 'occurrences' (integer ≥ 1).</summary>
  private static Result<(bool All, int Occurrences)> ParseSelector(JsonElement json)
  {
    bool hasAll = json.TryGetProperty(AllName, out JsonElement allEl);
    bool hasOccurrences = json.TryGetProperty(OccurrencesName, out JsonElement occurrencesEl);
    if (hasAll == hasOccurrences)
    {
      return Result.Failure<(bool, int)>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "Provide exactly one of 'all' (boolean true) or 'occurrences' (integer ≥ 1)."));
    }

    Result<(bool All, int Occurrences)> selector = hasAll
      ? RequireExplicitAll(allEl)
      : RequireOccurrences(occurrencesEl);
    return selector;
  }

  private static Result<(bool All, int Occurrences)> RequireExplicitAll(JsonElement allEl)
  {
    Result<(bool All, int Occurrences)> all = allEl.ValueKind is JsonValueKind.True
      ? Result.Success((true, 0))
      : Result.Failure<(bool, int)>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "'all' must be exactly true. Provide exactly one of " +
          "'all' (boolean true) or 'occurrences' (integer ≥ 1)."));
    return all;
  }

  private static Result<(bool All, int Occurrences)> RequireOccurrences(JsonElement occurrencesEl)
  {
    if (occurrencesEl.ValueKind != JsonValueKind.Number || !occurrencesEl.TryGetInt32(out int occurrences))
    {
      return Result.Failure<(bool, int)>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'{OccurrencesName}' must be a integer, but got {occurrencesEl.ValueKind}."));
    }

    Result<(bool All, int Occurrences)> positive = occurrences >= 1
      ? Result.Success((false, occurrences))
      : Result.Failure<(bool, int)>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'{OccurrencesName}' must be ≥ 1 (got {occurrences})."));
    return positive;
  }

  private static Result<EditToolInput> Fail(DomainError err) =>
      Result.Failure<EditToolInput>(err);
}
