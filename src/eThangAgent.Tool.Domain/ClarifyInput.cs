using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed record ClarifyInput(string Question, IReadOnlyList<string>? Options, bool AllowFreeText)
{
  private const string QuestionName = "question";
  private const string AllowFreeTextName = "allowFreeText";

  public static Result<ClarifyInput> Create(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Fail(baseParse.Error!);
    }

    JsonElement json = baseParse.Value;

    HashSet<string> known = new([QuestionName, "options", AllowFreeTextName, ToolTimeout.ParameterName], StringComparer.Ordinal);
    List<string> unknown = [.. json.EnumerateObject()
        .Where(p => !known.Contains(p.Name))
        .Select(p => p.Name)];
    if (unknown.Count > 0)
    {
      return Fail(new DomainError("UnknownParameter",
          $"Unknown parameter(s): {string.Join(", ", unknown)}. Allowed: question, options, allowFreeText, {ToolTimeout.ParameterName}."));
    }

    if (!json.TryGetProperty(QuestionName, out JsonElement questionEl))
    {
      return Missing(QuestionName);
    }

    if (questionEl.ValueKind != JsonValueKind.String)
    {
      return WrongType(QuestionName, "string", questionEl.ValueKind);
    }

    string question = questionEl.GetString()!;
    if (question.Length == 0)
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue, "'question' must be a non-empty string."));
    }

    IReadOnlyList<string>? options = null;
    if (json.TryGetProperty("options", out JsonElement optionsEl))
    {
      if (optionsEl.ValueKind != JsonValueKind.Array)
      {
        return Fail(new DomainError("InvalidParameterType",
            $"'options' must be an array of strings, but got {optionsEl.ValueKind}."));
      }

      List<string> items = [];
      foreach (JsonElement item in optionsEl.EnumerateArray())
      {
        if (item.ValueKind != JsonValueKind.String)
        {
          return Fail(new DomainError("InvalidParameterType",
              $"'options' must contain only strings, but got {item.ValueKind}."));
        }

        string option = item.GetString()!;
        if (option.Length == 0)
        {
          return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
              "'options' entries must be non-empty strings."));
        }

        items.Add(option);
      }
      if (items.Count < 2)
      {
        return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
            $"'options' must contain at least 2 entries when provided, but got {items.Count}."));
      }

      options = items;
    }

    if (!json.TryGetProperty(AllowFreeTextName, out JsonElement freeEl))
    {
      return Missing(AllowFreeTextName);
    }

    if (freeEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
    {
      return WrongType(AllowFreeTextName, "boolean", freeEl.ValueKind);
    }

    bool allowFreeText = freeEl.GetBoolean();

    // An options-free, free-text-blocked question can never succeed: every
    // answer would be rejected as FreeTextNotAllowed. Reject it at the boundary.
    return !allowFreeText && options is null
      ? Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "'allowFreeText' is false but 'options' was not provided: without options " +
          "every answer would be rejected as free text. Provide at least 2 options " +
          "or set 'allowFreeText' to true."))
      : Result.Success<ClarifyInput>(new(question, options, allowFreeText));
  }

  private static Result<ClarifyInput> Missing(string n) =>
      Result.Failure<ClarifyInput>(new DomainError("MissingParameter",
          $"Missing required parameter '{n}'. This tool requires question and allowFreeText; options is optional."));

  private static Result<ClarifyInput> WrongType(string n, string e, JsonValueKind a) =>
      Result.Failure<ClarifyInput>(new DomainError("InvalidParameterType",
          $"'{n}' must be a {e}, but got {a}."));

  private static Result<ClarifyInput> Fail(DomainError err) =>
      Result.Failure<ClarifyInput>(err);
}
