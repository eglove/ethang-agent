using System.Text.Json;
using eThangAgent.ModelDomain;

namespace eThangAgent.Cloudflare.ACL;

/// <summary>Translates one Cloudflare chat result into the harness's response value.
///     <para>THE DOCUMENTED SHAPE IS <c>{response, usage, tool_calls}</c>: the answer text under
///     <c>response</c>, the token counts under <c>usage</c>, and each tool call as
///     <c>{name, arguments}</c> with the arguments as an OBJECT and no id at all. A call is
///     therefore named by its position, because the wire gives nothing else to carry, and its
///     arguments are serialized to the JSON text the harness's tool dispatch reads.</para>
///     <para>A CALL WITH NO ARGUMENTS IS AN EMPTY OBJECT, not an absent one: the harness's tool
///     parser reads <c>{}</c> as 'no arguments supplied', which is what the model said.</para>
///     <para>NO USAGE REPORTED IS NULL, NEVER ZERO: a response that reported no token counts
///     said nothing about what it cost.</para></summary>
internal static class CloudflareResponse
{
  /// <summary>The response a result object describes.
  ///     <para>A RESULT THAT NAMES TOOL CALLS FINISHES AS ToolCalls, because the loop must
  ///     dispatch them; otherwise the result's own <c>finish_reason</c> decides, and a result
  ///     that states none is a completed turn.</para></summary>
  /// <param name="result">The response's <c>result</c> object.</param>
  public static ModelResponse Read(JsonElement result)
  {
    List<ToolCallRequest> calls = ToolCalls(result);
    return new ModelResponse(
        Text(result),
        calls,
        calls.Count > 0 ? FinishReason.ToolCalls : Finish(result),
        Usage(result))
    {
      ServedModelId = ServedModel(result),
    };
  }

  /// <summary>The answer text, or null when the model produced none.</summary>
  /// <param name="result">The response's result object.</param>
  private static string? Text(JsonElement result)
      => result.ValueKind == JsonValueKind.Object
          && result.TryGetProperty("response", out JsonElement response)
          && response.ValueKind == JsonValueKind.String
          && response.GetString() is { Length: > 0 } text
              ? text
              : null;

  /// <summary>Every tool call the model requested, in the order it requested them.
  ///     <para>A CALL THAT NAMES NO TOOL IS DROPPED: a call with an empty name cannot be
  ///     dispatched, and returning it would fail the turn on a call the model never made.
  ///     </para></summary>
  /// <param name="result">The response's result object.</param>
  private static List<ToolCallRequest> ToolCalls(JsonElement result)
  {
    if (result.ValueKind != JsonValueKind.Object
        || !result.TryGetProperty("tool_calls", out JsonElement calls)
        || calls.ValueKind != JsonValueKind.Array)
    {
      return [];
    }

    List<ToolCallRequest> requests = [];
    foreach (JsonElement call in calls.EnumerateArray())
    {
      if (Name(call) is { Length: > 0 } name)
      {
        requests.Add(new ToolCallRequest(
            $"cloudflare-tool-{requests.Count}",
            name,
            Arguments(call)));
      }
    }

    return requests;
  }

  /// <summary>The tool name a call states, or null when it states none.</summary>
  /// <param name="call">One element of the result's <c>tool_calls</c> array.</param>
  private static string? Name(JsonElement call)
      => call.ValueKind == JsonValueKind.Object
          && call.TryGetProperty("name", out JsonElement name)
          && name.ValueKind == JsonValueKind.String
              ? name.GetString()
              : null;

  /// <summary>The call's arguments as the JSON text the harness's tool dispatch reads: an object
  ///     is serialized, a string is taken as it arrived, and a call that states none is the empty
  ///     object.</summary>
  /// <param name="call">One element of the result's <c>tool_calls</c> array.</param>
  private static string Arguments(JsonElement call)
      => call.ValueKind == JsonValueKind.Object
          && call.TryGetProperty("arguments", out JsonElement arguments)
              ? ArgumentText(arguments)
              : "{}";

  /// <summary>The argument text one value contributes: an object is serialized, a non-empty
  ///     string is taken as it arrived, and anything else is the empty object.</summary>
  /// <param name="arguments">The argument value.</param>
  private static string ArgumentText(JsonElement arguments)
      => arguments.ValueKind == JsonValueKind.Object
          ? arguments.GetRawText()
          : TextOrEmptyObject(arguments);

  /// <summary>A string value taken as it arrived, or the empty object for anything else.
  ///     </summary>
  /// <param name="arguments">The argument value.</param>
  private static string TextOrEmptyObject(JsonElement arguments)
      => arguments.ValueKind == JsonValueKind.String
          && arguments.GetString() is { Length: > 0 } text
              ? text
              : "{}";

  /// <summary>The token counts the result reports, or null when it reports none.</summary>
  /// <param name="result">The response's result object.</param>
  private static TokenUsage? Usage(JsonElement result)
      => result.ValueKind == JsonValueKind.Object
          && result.TryGetProperty("usage", out JsonElement usage)
          && usage.ValueKind == JsonValueKind.Object
              ? CloudflareUsage.Read(usage)
              : null;

  /// <summary>The finish reason the result states, or a completed turn when it states none.
  ///     </summary>
  /// <param name="result">The response's result object.</param>
  private static FinishReason Finish(JsonElement result)
      => result.ValueKind == JsonValueKind.Object
          && result.TryGetProperty("finish_reason", out JsonElement reason)
          && reason.ValueKind == JsonValueKind.String
              ? CloudflareFinishReason.Of(reason.GetString())
              : FinishReason.Stop;

  /// <summary>The model the result reports as having served it, or null when it names none.
  ///     </summary>
  /// <param name="result">The response's result object.</param>
  private static string? ServedModel(JsonElement result)
      => result.ValueKind == JsonValueKind.Object
          && result.TryGetProperty("model", out JsonElement model)
          && model.ValueKind == JsonValueKind.String
          && model.GetString() is { Length: > 0 } served
              ? served
              : null;
}
