using System.Globalization;
using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>The required per-call execution budget every tool argument object must carry.
///     Key: <c>timeoutSeconds</c> — a positive integer number of seconds, strictly
///     validated (missing, wrong type, non-positive, or above the cap is a typed error;
///     nothing is coerced, defaulted, or clamped). When the budget elapses the call fails
///     with <c>Error [ToolTimeout]</c>; cancellation from the caller still wins.</summary>
public static class ToolTimeout
{
  /// <summary>Upper bound for a single tool call. Generous by design — the point of the
  ///     parameter is an explicit budget, not a tighter leash than the old global caps.</summary>
  public const int MaxSeconds = 3600;

  public const string ParameterName = "timeoutSeconds";

  /// <summary>Description text every ToolDefinition must advertise so the model never
  ///     has to guess what it is looking at.</summary>
  public const string ParameterDescription =
      "Required execution budget in whole seconds, 1..3600. " +
      "The call fails with Error [ToolTimeout] if it exceeds this budget.";

  /// <summary>Parses and validates the timeout key on an already-parsed JSON argument
  ///     object. Returns the budget or a typed error suitable for verbatim surfacing.</summary>
  public static Result<TimeSpan> Parse(JsonElement json)
  {
    return !json.TryGetProperty(ParameterName, out JsonElement el)
      ? Result.Failure<TimeSpan>(new DomainError("MissingParameter",
          "Missing required parameter '" + ParameterName +
          "'. Every tool call must state its execution budget in seconds."))
      : el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out int seconds)
      ? Result.Failure<TimeSpan>(new DomainError("InvalidParameterType",
          "'" + ParameterName + "' must be an integer number of seconds, but got " +
          el.ValueKind + "."))
      : seconds < 1
      ? Result.Failure<TimeSpan>(new DomainError("InvalidParameterValue",
          "'" + ParameterName + "' must be ≥ 1 second (got " + seconds + ")."))
      : seconds > MaxSeconds
      ? Result.Failure<TimeSpan>(new DomainError("InvalidParameterValue",
          "'" + ParameterName + "' must be ≤ " + MaxSeconds +
          " seconds (got " + seconds + ")."))
      : Result.Success<TimeSpan>(TimeSpan.FromSeconds(seconds));
  }

  /// <summary>Formats the standard error result for an exceeded budget. The message
  ///     documents the contract exactly; the model can re-issue with a larger budget.</summary>
  public static ToolResult TimedOut(string toolName, TimeSpan budget) =>
      new("Error [ToolTimeout]: '" + toolName + "' exceeded its " +
          budget.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "s execution budget and was stopped. " +
          "Re-issue with a larger '" + ParameterName +
          "' if the work genuinely needs longer.", true);
}
