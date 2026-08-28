using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed record ClarifyInput(string Question, IReadOnlyList<string>? Options, bool AllowFreeText)
{
  private const string QuestionName = "question";
  private const string OptionsName = "options";
  private const string AllowFreeTextName = "allowFreeText";
  private const string RequirementText = "This tool requires question and allowFreeText; options is optional.";

  private static readonly string[] AllowedNames =
      [QuestionName, OptionsName, AllowFreeTextName, ToolTimeout.ParameterName];

  public static Result<ClarifyInput> Create(string jsonArguments)
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

    Result<string> question = ToolArguments.RequireString(json, QuestionName, RequirementText);
    if (!question.IsSuccess)
    {
      return Fail(question.Error);
    }

    DomainError? emptyQuestion = question.Value.Length == 0
      ? new DomainError(ToolErrorCodes.InvalidParameterValue, "'question' must be a non-empty string.")
      : null;
    if (emptyQuestion is not null)
    {
      return Fail(emptyQuestion);
    }

    Result<IReadOnlyList<string>?> options = ParseOptions(json);
    if (!options.IsSuccess)
    {
      return Fail(options.Error);
    }

    Result<bool> allowFreeText = ToolArguments.RequireBool(json, AllowFreeTextName, RequirementText);
    if (!allowFreeText.IsSuccess)
    {
      return Fail(allowFreeText.Error);
    }

    // An options-free, free-text-blocked question can never succeed: every
    // answer would be rejected as FreeTextNotAllowed. Reject it at the boundary.
    Result<ClarifyInput> result = !allowFreeText.Value && options.ValueOrNull is null
      ? Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "'allowFreeText' is false but 'options' was not provided: without options " +
          "every answer would be rejected as free text. Provide at least 2 options " +
          "or set 'allowFreeText' to true."))
      : Result.Success<ClarifyInput>(new(question.Value, options.Value, allowFreeText.Value));
    return result;
  }

  private static Result<IReadOnlyList<string>?> ParseOptions(JsonElement json)
  {
    if (!json.TryGetProperty(OptionsName, out JsonElement optionsEl))
    {
      return Result.Success<IReadOnlyList<string>?>(null);
    }

    if (optionsEl.ValueKind != JsonValueKind.Array)
    {
      return Result.Failure<IReadOnlyList<string>?>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'{OptionsName}' must be an array of strings, but got {optionsEl.ValueKind}."));
    }

    List<string> items = [];
    foreach (JsonElement item in optionsEl.EnumerateArray())
    {
      DomainError? itemError = ValidateOption(item);
      if (itemError is not null)
      {
        return Result.Failure<IReadOnlyList<string>?>(itemError);
      }

      items.Add(item.GetString()!);
    }

    Result<IReadOnlyList<string>?> enough = items.Count >= 2
      ? Result.Success<IReadOnlyList<string>?>(items)
      : Result.Failure<IReadOnlyList<string>?>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'{OptionsName}' must contain at least 2 entries when provided, but got {items.Count}."));
    return enough;
  }

  /// <summary>Per-entry rules: string kind, then non-empty.</summary>
  private static DomainError? ValidateOption(JsonElement item)
  {
    if (item.ValueKind != JsonValueKind.String)
    {
      return new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'{OptionsName}' must contain only strings, but got {item.ValueKind}.");
    }

    DomainError? empty = item.GetString()!.Length == 0
      ? new DomainError(ToolErrorCodes.InvalidParameterValue,
          "'options' entries must be non-empty strings.")
      : null;
    return empty;
  }

  private static Result<ClarifyInput> Fail(DomainError err) =>
      Result.Failure<ClarifyInput>(err);
}
