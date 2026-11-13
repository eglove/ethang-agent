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
    if (!json.TryGetProperty(ParameterName, out JsonElement el))
    {
      return Result.Failure<TimeSpan>(new DomainError("MissingParameter",
          "Missing required parameter '" + ParameterName +
          "'. Every tool call must state its execution budget in seconds."));
    }

    if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out int seconds))
    {
      return Result.Failure<TimeSpan>(new DomainError("InvalidParameterType",
          "'" + ParameterName + "' must be an integer number of seconds, but got " +
          el.ValueKind + "."));
    }

    if (seconds < 1)
    {
      return Result.Failure<TimeSpan>(new DomainError("InvalidParameterValue",
          "'" + ParameterName + "' must be ≥ 1 second (got " + seconds + ")."));
    }

    if (seconds > MaxSeconds)
    {
      return Result.Failure<TimeSpan>(new DomainError("InvalidParameterValue",
          "'" + ParameterName + "' must be ≤ " + MaxSeconds +
          " seconds (got " + seconds + ")."));
    }

    TimeSpan budget = TimeSpan.FromSeconds(seconds);
    return Result.Success(budget);
  }

  /// <summary>Formats the standard error result for an exceeded budget. The message
  ///     attributes the budget to the caller — it is the caller's own timeoutSeconds
  ///     parameter, not a tool-internal cap — so the failure is never misattributed, and
  ///     names the narrowing move rather than a larger budget: a run that overruns is
  ///     almost always a command too big to be one step, and raising the budget only
  ///     lengthens the wait before the same failure.</summary>
  public static ToolResult TimedOut(string toolName, TimeSpan budget) =>
      new(Message(toolName, budget), true);

  /// <summary>The standard timeout result, carrying what the run had already printed.
  ///     An abandoned run's output is the only account of how far it got, so it rides the
  ///     failure rather than being lost with it: the caller sees the progress AND the
  ///     reason it stopped, in one result.</summary>
  public static ToolResult TimedOut(string toolName, TimeSpan budget, ExecOutputProgress? progress)
  {
    string printed = progress?.Render() ?? string.Empty;
    return printed.Length == 0
        ? TimedOut(toolName, budget)
        : new ToolResult(Message(toolName, budget) +
            "\n[exec: output printed before the budget elapsed — the run was abandoned mid-flight, " +
            "so this is everything it printed, not a complete run]\n" + printed, true);
  }

  private static string Message(string toolName, TimeSpan budget) =>
      "Error [ToolTimeout]: '" + toolName + "' exceeded the " +
      budget.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) +
      "s execution budget you set via timeoutSeconds. Narrow the command, or split the " +
      "work into smaller runs that each finish well inside a budget.";
}
