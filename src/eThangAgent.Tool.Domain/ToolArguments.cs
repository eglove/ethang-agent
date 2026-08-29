using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Shared JSON-argument parsing used by every tool. <see cref="ParseObject"/>
///     performs the strict object check every argument object must pass and is what the
///     per-tool input parsers build on (the optional <c>timeoutSeconds</c> key belongs
///     to their allowed sets). The mandatory-budget requirement itself is enforced once,
///     at the dispatch boundary, via <see cref="ToolCallEnvelopeParser"/>/
///     <see cref="ToolExecution"/> — never silently defaulted.</summary>
public static class ToolArguments
{
  /// <summary>Parses the raw arguments into a cloned JSON element, rejecting malformed
  ///     JSON and non-object input with typed errors.</summary>
  public static Result<JsonElement> ParseObject(string jsonArguments)
  {
    JsonElement json;
    try
    {
      using JsonDocument doc = JsonDocument.Parse(jsonArguments);
      json = doc.RootElement.Clone();
    }
    catch (JsonException ex)
    {
      return Result.Failure<JsonElement>(new DomainError("InvalidJsonArguments",
          $"Arguments are not valid JSON: {ex.Message}"));
    }
    return json.ValueKind != JsonValueKind.Object
      ? Result.Failure<JsonElement>(new DomainError("InvalidJsonArguments",
          "Arguments must be a JSON object."))
      : Result.Success(json);
  }

  /// <summary>Strict object check plus the mandatory <c>timeoutSeconds</c> budget.
  ///     Used by callers that own the whole contract in one place (zero-parameter
  ///     tools' argument checks).</summary>
  public static Result<(JsonElement Json, TimeSpan Timeout)> Parse(string jsonArguments)
  {
    Result<JsonElement> baseParse = ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Result.Failure<(JsonElement, TimeSpan)>(baseParse.Error);
    }

    Result<TimeSpan> timeout = ToolTimeout.Parse(baseParse.Value);
    return !timeout.IsSuccess
      ? (Result<(JsonElement Json, TimeSpan Timeout)>)Result.Failure<(JsonElement, TimeSpan)>(timeout.Error)
      : (Result<(JsonElement Json, TimeSpan Timeout)>)Result.Success((baseParse.Value, timeout.Value));
  }

  /// <summary>The validated execution budget for arguments that passed <see cref="Parse"/>.</summary>
  public static TimeSpan TimeoutOf(JsonElement json) =>
      ToolTimeout.Parse(json).Value;

  /// <summary>Rejects argument objects carrying keys outside <paramref name="allowed"/>
  ///     (the mandatory <c>timeoutSeconds</c> budget key belongs to every allowed set).
  ///     Returns the typed error, or null when every supplied key is known. The allowed
  ///     list is echoed verbatim, in the given order, so the model can self-correct.</summary>
  public static DomainError? RejectUnknownParameters(JsonElement json, params string[] allowed)
  {
    HashSet<string> known = new(allowed, StringComparer.Ordinal);
    List<string> unknown = [.. json.EnumerateObject()
        .Where(p => !known.Contains(p.Name))
        .Select(p => p.Name)];
    return unknown.Count == 0
      ? null
      : new DomainError(ToolErrorCodes.UnknownParameter,
          $"Unknown parameter(s): {string.Join(", ", unknown)}. Allowed: {string.Join(", ", allowed)}.");
  }

  /// <summary>Typed failure for an absent required parameter. The requirement tail is
  ///     tool-specific and must end the message exactly as the tool advertises it.</summary>
  public static Result<T> Missing<T>(string name, string requirement) =>
      Result.Failure<T>(new DomainError(ToolErrorCodes.MissingParameter,
          $"Missing required parameter '{name}'. {requirement}"));

  /// <summary>Reads a required JSON string parameter: present and of string kind.
  ///     Emptiness is a value rule left to the caller.</summary>
  public static Result<string> RequireString(JsonElement json, string name, string requirement)
  {
    return json.TryGetProperty(name, out JsonElement el)
      ? StringOf(el, name)
      : Missing<string>(name, requirement);
  }

  /// <summary>Reads an optional JSON string parameter: null when absent, a typed error
  ///     when present but not a string.</summary>
  public static Result<string?> OptionalString(JsonElement json, string name)
  {
    if (!json.TryGetProperty(name, out JsonElement el))
    {
      return Result.Success<string?>(null);
    }

    Result<string> text = StringOf(el, name);
    return !text.IsSuccess
      ? Result.Failure<string?>(text.Error)
      : Result.Success<string?>(text.Value);
  }

  /// <summary>Reads a required JSON integer parameter. Range checks are value rules
  ///     left to the caller — nothing is coerced or clamped here.</summary>
  public static Result<int> RequireInt(JsonElement json, string name, string requirement)
  {
    return json.TryGetProperty(name, out JsonElement el)
      ? IntOf(el, name)
      : Missing<int>(name, requirement);
  }

  /// <summary>Reads an optional JSON integer parameter: null when absent, a typed
  ///     error when present but not an integer.</summary>
  public static Result<int?> OptionalInt(JsonElement json, string name)
  {
    if (!json.TryGetProperty(name, out JsonElement el))
    {
      return Result.Success<int?>(null);
    }

    Result<int> value = IntOf(el, name);
    return !value.IsSuccess
      ? Result.Failure<int?>(value.Error)
      : Result.Success<int?>(value.Value);
  }

  /// <summary>Reads a required JSON boolean parameter: exactly <c>true</c> or
  ///     <c>false</c>, never a truthy stand-in.</summary>
  public static Result<bool> RequireBool(JsonElement json, string name, string requirement)
  {
    if (!json.TryGetProperty(name, out JsonElement el))
    {
      return Missing<bool>(name, requirement);
    }

    Result<bool> value = el.ValueKind is JsonValueKind.True or JsonValueKind.False
      ? Result.Success(el.GetBoolean())
      : Result.Failure<bool>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'{name}' must be a boolean, but got {el.ValueKind}."));
    return value;
  }

  /// <summary>Reads an optional JSON boolean parameter: null when absent, a typed
  ///     error when present but not exactly true or false.</summary>
  public static Result<bool?> OptionalBool(JsonElement json, string name)
  {
    if (!json.TryGetProperty(name, out JsonElement el))
    {
      return Result.Success<bool?>(null);
    }

    Result<bool> value = el.ValueKind is JsonValueKind.True or JsonValueKind.False
      ? Result.Success(el.GetBoolean())
      : Result.Failure<bool>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'{name}' must be a boolean, but got {el.ValueKind}."));
    return !value.IsSuccess
      ? Result.Failure<bool?>(value.Error)
      : Result.Success<bool?>(value.Value);
  }

  /// <summary>Reads an optional JSON array-of-strings parameter: null when absent,
  ///     a typed error when present but not an array of strings. Entries pass
  ///     through verbatim — emptiness and content are caller value rules.</summary>
  public static Result<IReadOnlyList<string>?> OptionalStringArray(JsonElement json, string name)
  {
    if (!json.TryGetProperty(name, out JsonElement el))
    {
      return Result.Success<IReadOnlyList<string>?>(null);
    }

    if (el.ValueKind != JsonValueKind.Array)
    {
      return Result.Failure<IReadOnlyList<string>?>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'{name}' must be an array of strings, but got {el.ValueKind}."));
    }

    List<string> items = [];
    foreach (JsonElement item in el.EnumerateArray())
    {
      if (item.ValueKind != JsonValueKind.String)
      {
        return Result.Failure<IReadOnlyList<string>?>(new DomainError(ToolErrorCodes.InvalidParameterType,
            $"'{name}' must contain only strings, but got {item.ValueKind}."));
      }

      items.Add(item.GetString()!);
    }

    return Result.Success<IReadOnlyList<string>?>(items);
  }

  /// <summary>Parses an enum-typed string by exact ordinal match against
  ///     <paramref name="allowedNames"/> — no case folding, no numeric fallback.</summary>
  public static Result<T> ParseEnum<T>(string name, string text, IReadOnlyList<string> allowedNames)
      where T : struct, Enum
  {
    ArgumentNullException.ThrowIfNull(allowedNames);
    string? match = allowedNames.FirstOrDefault(allowed =>
        string.Equals(text, allowed, StringComparison.Ordinal));
    Result<T> result = match is null
      ? Result.Failure<T>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'{name}' must be exactly one of {string.Join(", ", allowedNames)} (case-sensitive), but got '{text}'."))
      : Result.Success(Enum.Parse<T>(match));
    return result;
  }

  /// <summary>Validates one element as a JSON string. The type-mismatch wording is the
  ///     shared tool contract: <c>'{name}' must be a string, but got {kind}.</c></summary>
  private static Result<string> StringOf(JsonElement el, string name)
  {
    Result<string> text = el.ValueKind == JsonValueKind.String
      ? Result.Success(el.GetString()!)
      : Result.Failure<string>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'{name}' must be a string, but got {el.ValueKind}."));
    return text;
  }

  /// <summary>Validates one element as a JSON integer. The type-mismatch wording is the
  ///     shared tool contract: <c>'{name}' must be a integer, but got {kind}.</c>
  ///     (The article reproduces the message verbatim as every tool advertises it.)</summary>
  private static Result<int> IntOf(JsonElement el, string name)
  {
    Result<int> value = el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int parsed)
      ? Result.Success(parsed)
      : Result.Failure<int>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'{name}' must be a integer, but got {el.ValueKind}."));
    return value;
  }
}
