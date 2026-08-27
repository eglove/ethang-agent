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
      return Result.Failure<(JsonElement, TimeSpan)>(baseParse.Error!);
    }

    Result<TimeSpan> timeout = ToolTimeout.Parse(baseParse.Value);
    return !timeout.IsSuccess
      ? (Result<(JsonElement Json, TimeSpan Timeout)>)Result.Failure<(JsonElement, TimeSpan)>(timeout.Error!)
      : (Result<(JsonElement Json, TimeSpan Timeout)>)Result.Success((baseParse.Value, timeout.Value));
  }

  /// <summary>The validated execution budget for arguments that passed <see cref="Parse"/>.</summary>
  public static TimeSpan TimeoutOf(JsonElement json) =>
      ToolTimeout.Parse(json).Value;
}
