using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed record ToolCallEnvelope(string ToolName, TimeSpan Timeout, JsonElement Arguments);

public static class ToolCallEnvelopeParser
{
  /// <summary>Parses a tool call's raw JSON arguments into the shared envelope: the tool
  ///     name (from the call itself) plus the mandatory timeout budget. The arguments
  ///     element is returned untouched for the tool's own parser. Malformed JSON,
  ///     non-object input, and timeout violations are typed errors.</summary>
  public static Result<ToolCallEnvelope> Parse(string toolName, string jsonArguments)
  {
    Result<JsonElement> parsed = ToolArguments.ParseObject(jsonArguments);
    if (!parsed.IsSuccess)
    {
      return Result.Failure<ToolCallEnvelope>(parsed.Error!);
    }

    Result<TimeSpan> budget = ToolTimeout.Parse(parsed.Value);
    return !budget.IsSuccess
      ? Result.Failure<ToolCallEnvelope>(budget.Error!)
      : Result.Success(
        new ToolCallEnvelope(toolName, budget.Value, parsed.Value));
  }
}
