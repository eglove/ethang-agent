using System.Text;
using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Provider.Wire;

/// <summary>Wire-level core for the OpenAI Responses API: builds the <c>input</c> item
///     array (<see cref="BuildInput"/>) and the flat function-tool entries
///     (<see cref="TranslateTool"/>), and parses a non-streaming response body
///     (<see cref="ParseResponse"/>). Shared deliberately: the request/parse plumbing
///     is byte-identical across Responses-API providers and lives OUTSIDE the domain,
///     so the ACLs-share-no-domain-code doctrine (AGENTS.md) stays intact. Verified
///     against the live OpenRouter responses endpoint (2026-09): input items are
///     accepted WITHOUT ids/status; tool results ride <c>function_call_output</c> with
///     a string or an input-part array; image parts on tool results are native — the
///     chat-completions image projection workaround has no equivalent here.</summary>
public static class ResponsesApiRequestCore
{
  /// <summary>Parses a non-streaming response body: assembles content from message
  ///     items' output_text parts and tool calls from function_call items; the body
  ///     status (with its incomplete_details reason) maps to the finish reason —
  ///     function calls imply ToolCalls regardless of status.</summary>
  public static Result<ModelResponse> ParseResponse(JsonElement body)
  {
    if (!body.TryGetProperty("output", out JsonElement output) || output.ValueKind != JsonValueKind.Array)
    {
      throw new InvalidOperationException("Provider response contains no output array.");
    }

    StringBuilder content = new();
    StringBuilder reasoning = new();
    List<ToolCallRequest> toolCalls = [];
    List<ServerToolCall> serverToolCalls = [];
    foreach (JsonElement item in output.EnumerateArray())
    {
      AppendOutputItem(item, content, reasoning, toolCalls, serverToolCalls);
    }

    return Result.Success(new ModelResponse(
        content.Length > 0 ? content.ToString() : null,
        toolCalls,
        ParseFinishReason(body, toolCalls.Count > 0),
        ParseUsage(body),
        serverToolCalls)
    {
      // Null when the response carried no reasoning item at all — an absent trace is a
      // different fact from an empty one, the rule TokenUsage's optional counts follow.
      Reasoning = reasoning.Length > 0 ? reasoning.ToString() : null,
      ServedModelId = ParseServedModelId(body),
      ServedProvider = ParseServedProvider(body),
      ServedServiceTier = ParseServiceTier(body),
    });
  }

  /// <summary>Classifies one output item into its sink: message text, the reasoning
  ///     trace (the non-streaming sibling of the stream's reasoning deltas, issue #263),
  ///     a function call, or a surfaced server-tool call. An unrecognized type is
  ///     ignored (the tolerance contract).</summary>
  private static void AppendOutputItem(JsonElement item, StringBuilder content, StringBuilder reasoning,
      List<ToolCallRequest> toolCalls, List<ServerToolCall> serverToolCalls)
  {
    string type = item.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String
        ? t.GetString()! : "";
    switch (type)
    {
      case "message":
        AppendMessageContent(item, content);
        break;
      case "reasoning":
        AppendReasoningContent(item, reasoning);
        break;
      case "function_call":
        toolCalls.Add(FunctionToolCall(item));
        break;
      default:
        if (ServerToolCallItem.TryParse(type, item, out ServerToolCall? call))
        {
          serverToolCalls.Add(call);
        }

        break;
    }
  }

  /// <summary>One function_call output item: the call_id and name are required wire
  ///     members, the arguments string defaults to empty when absent.</summary>
  private static ToolCallRequest FunctionToolCall(JsonElement item)
  {
    string arguments = item.TryGetProperty("arguments", out JsonElement args) && args.ValueKind == JsonValueKind.String
        ? args.GetString() ?? "" : "";
    return new ToolCallRequest(
        item.GetProperty("call_id").GetString()!,
        item.GetProperty("name").GetString()!,
        arguments);
  }

  /// <summary>Maps the response status to the finish reason. "completed" (or a missing
  ///     status) maps to Stop; "incomplete" maps through incomplete_details.reason —
  ///     max_output_tokens → Length, content_filter → ContentFilter, else Unknown. A
  ///     request that produced tool calls finishes as ToolCalls: the loop must dispatch
  ///     them, and the responses body never says "tool_calls" the way
  ///     chat-completions' finish_reason did.</summary>
  private static FinishReason ParseFinishReason(JsonElement body, bool hasToolCalls)
  {
    if (hasToolCalls)
    {
      return FinishReason.ToolCalls;
    }

    if (!body.TryGetProperty("status", out JsonElement status) || status.ValueKind != JsonValueKind.String)
    {
      return FinishReason.Stop;
    }

    FinishReason reason = FinishReason.Unknown;
    switch (status.GetString())
    {
      case "completed":
        reason = FinishReason.Stop;
        break;
      case "incomplete":
        reason = IncompleteReason(body);
        break;
      default:
        break;
    }

    return reason;
  }

  private static FinishReason IncompleteReason(JsonElement body)
  {
    FinishReason reason = FinishReason.Unknown;
    if (body.TryGetProperty("incomplete_details", out JsonElement details)
        && details.ValueKind == JsonValueKind.Object
        && details.TryGetProperty("reason", out JsonElement reasonEl)
        && reasonEl.ValueKind == JsonValueKind.String)
    {
      switch (reasonEl.GetString())
      {
        case "max_output_tokens":
          reason = FinishReason.Length;
          break;
        case "content_filter":
          reason = FinishReason.ContentFilter;
          break;
        default:
          break;
      }
    }

    return reason;
  }

  private static void AppendMessageContent(JsonElement item, StringBuilder content)
  {
    if (!item.TryGetProperty("content", out JsonElement contentEl) || contentEl.ValueKind != JsonValueKind.Array)
    {
      return;
    }

    foreach (JsonElement part in contentEl.EnumerateArray())
    {
      if (part.TryGetProperty("type", out JsonElement partType)
          && partType.ValueKind == JsonValueKind.String
          && partType.GetString() == "output_text"
          && part.TryGetProperty("text", out JsonElement text)
          && text.ValueKind == JsonValueKind.String)
      {
        _ = content.Append(text.GetString());
      }
    }
  }

  /// <summary>Accumulates one reasoning output item's text (issue #263). The item
  ///     carries the raw trace under <c>content</c> (parts typed <c>reasoning_text</c>)
  ///     and/or the provider's summary under <c>summary</c> (parts typed
  ///     <c>summary_text</c>). The RAW trace wins when both are present — the summary is
  ///     the display-channel artifact, the trace is the evidence — and a provider that
  ///     emits only a summary still yields a trace rather than nothing.</summary>
  private static void AppendReasoningContent(JsonElement item, StringBuilder reasoning)
  {
    string raw = ReasoningParts(item, "content", "reasoning_text");
    _ = reasoning.Append(raw.Length > 0 ? raw : ReasoningParts(item, "summary", "summary_text"));
  }

  /// <summary>The concatenated text of one reasoning item's part array, or an empty
  ///     string when the array is absent or carries no matching part type.</summary>
  private static string ReasoningParts(JsonElement item, string member, string partType)
  {
    if (!item.TryGetProperty(member, out JsonElement parts) || parts.ValueKind != JsonValueKind.Array)
    {
      return "";
    }

    StringBuilder text = new();
    foreach (JsonElement part in parts.EnumerateArray())
    {
      if (part.TryGetProperty("type", out JsonElement type)
          && type.ValueKind == JsonValueKind.String
          && type.GetString() == partType
          && part.TryGetProperty("text", out JsonElement partText)
          && partText.ValueKind == JsonValueKind.String)
      {
        _ = text.Append(partText.GetString());
      }
    }

    return text.ToString();
  }

  /// <summary>Maps the Responses-API usage object (input_tokens / output_tokens /
  ///     input_tokens_details.cached_tokens / server_tool_use.web_search_requests /
  ///     output_tokens_details.reasoning_tokens) into TokenUsage; null when absent or
  ///     missing its primary counts.</summary>
  /// <para>Every optional count stays NULL when the provider omits it (issue #262): an
  ///     unreported reasoning or cache count is never fabricated as zero, so a provider
  ///     reporting <c>reasoning_tokens: 0</c> stays distinguishable from one reporting
  ///     nothing.</para>
  public static TokenUsage? ParseUsage(JsonElement parent)
  {
    if (!parent.TryGetProperty("usage", out JsonElement u) || u.ValueKind != JsonValueKind.Object)
    {
      return null;
    }

    if (!TryGetInt(u, "input_tokens", out int input) || !TryGetInt(u, "output_tokens", out int output))
    {
      return null;
    }

    int? cached = OptionalCount(u, "input_tokens_details", "cached_tokens");
    int? serverToolCalls = OptionalCount(u, "server_tool_use", "web_search_requests");
    int? reasoning = OptionalCount(u, "output_tokens_details", "reasoning_tokens");
    return new TokenUsage(input, output, cached, serverToolCalls, reasoning);
  }

  /// <summary>Reads the response object's <c>model</c> field — the model the provider
  ///     reports as having actually served this response (issue #290) — or null when the
  ///     provider names none.
  ///     <para>This is a DIFFERENT FACT from the requested model the request carried, and
  ///     the whole point of reading it is that a router makes the two diverge. An absent
  ///     field, a null, and an empty string all mean UNREPORTED and all read as null: a
  ///     blank model id would be a name that names nothing, and coercing any of them onto
  ///     the requested id would fabricate the very attribution this field exists to make
  ///     honest.</para>
  ///     <para>Shared by both parse paths deliberately: the non-streaming body and the
  ///     streaming terminal event carry the same response object, so one reader keeps them
  ///     from disagreeing about what answered.</para></summary>
  public static string? ParseServedModelId(JsonElement parent)
  {
    return parent.TryGetProperty("model", out JsonElement model)
        && model.ValueKind == JsonValueKind.String
        && model.GetString() is { Length: > 0 } served
        ? served
        : null;
  }

  /// <summary>Reads the response object's top-level <c>service_tier</c> — the capacity tier
  ///     the provider reports as having ACTUALLY served this response — or null when it
  ///     reports none.
  ///     <para>THE SMOKE TEST FOR A TIER PIN (OpenRouter service-tiers docs: "The API response
  ///     includes a service_tier field that indicates which capacity tier was actually used";
  ///     on the Responses API it sits at the TOP LEVEL of the response object). A session that
  ///     pinned <c>deepinfra/fp8/flex</c> has no other way to confirm the flex endpoint served
  ///     it: the request pin states an INTENTION, and this field is the only fact the wire
  ///     returns. Documented values: default, flex, priority, ultrafast.</para>
  ///     <para>NULL IS A REAL ANSWER, NEVER A FABRICATION: the docs state null means "no
  ///     service tier is available from upstream", so an absent member and a JSON null both
  ///     read as null. Coercing either onto the request's requested tier would repeat exactly
  ///     the pin-as-proof mistake ParseServedProvider exists to prevent.</para>
  ///     <para>Shared by both parse paths deliberately, for the same reason the sibling
  ///     readers are: the non-streaming body and the streaming terminal event carry the same
  ///     response object, so one reader keeps them from disagreeing.</para></summary>
  public static string? ParseServiceTier(JsonElement parent)
  {
    return parent.TryGetProperty("service_tier", out JsonElement tier)
        && tier.ValueKind == JsonValueKind.String
        && tier.GetString() is { Length: > 0 } served
        ? served
        : null;
  }

  /// <summary>Reads the response object's serving-provider name (issue #292), or null when
  ///     it names none.
  ///     <para>MEASURED 2026-10-27: this surface names no provider at all. Six live models —
  ///     two of them routers that resolve to a concrete model — returned an identical key set
  ///     with no <c>provider</c> member, and the <c>X-Provider-Name</c> header was absent. So
  ///     null is the ordinary answer here, and it is an ANSWER: the upstream vendor is not
  ///     something this wire reports. The reader exists so a provider that starts naming one
  ///     is recorded verbatim instead of being lost, and so both parse paths agree.</para>
  ///     <para>Absent, null, and empty all mean UNREPORTED and all read as null — the same
  ///     rule <see cref="ParseServedModelId"/> follows, for the same reason: a blank name is
  ///     a name that names nothing, and coercing it onto the request's pin would fabricate
  ///     the attribution this member exists to keep honest.</para></summary>
  public static string? ParseServedProvider(JsonElement parent)
  {
    return parent.TryGetProperty("provider", out JsonElement provider)
        && provider.ValueKind == JsonValueKind.String
        && provider.GetString() is { Length: > 0 } served
        ? served
        : null;
  }

  /// <summary>An optional nested count: <c>parent.member.countName</c> when the nested
  ///     object and its numeric count member are present, null otherwise.</summary>
  private static int? OptionalCount(JsonElement parent, string member, string countName)
  {
    return parent.TryGetProperty(member, out JsonElement nested)
        && nested.ValueKind == JsonValueKind.Object
        && TryGetInt(nested, countName, out int value)
        ? value : null;
  }

  private static bool TryGetInt(JsonElement parent, string name, out int value)
  {
    value = 0;
    return parent.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value);
  }

  /// <summary>Builds the request's <c>input</c> item array. The system prompt travels
  ///     as the body's top-level <c>instructions</c> (the caller reads
  ///     <see cref="ModelRequest.SystemPrompt"/> itself) and never as an item here;
  ///     System-role messages inside the history (compaction summaries) ride as
  ///     system-role message items. Assistant tool-call blocks translate to flat
  ///     function_call items — the responses wire pairs results by call_id, not by
  ///     adjacency — and tool results carry their image parts natively inside the
  ///     function_call_output part array.</summary>
  public static object[] BuildInput(ModelRequest request)
  {
    ArgumentNullException.ThrowIfNull(request);
    List<object> items = [];
    foreach (Message m in request.Messages)
    {
      switch (m.Role)
      {
        case Role.System:
          items.Add(MessageItem("system", InputContent(m)));
          break;
        case Role.User:
          items.Add(MessageItem("user", InputContent(m)));
          break;
        case Role.Assistant when m.Content is { Length: > 0 }:
          items.Add(new Dictionary<string, object?>
          {
            ["type"] = "message",
            ["role"] = "assistant",
            ["content"] = new object[] { new Dictionary<string, object?> { ["type"] = "output_text", ["text"] = m.Content } },
          });
          break;
        case Role.Assistant:
          break; // textless assistant shell — its tool calls translate below
        case Role.Tool:
          items.Add(FunctionCallOutput(m));
          break;
        default:
          throw new ArgumentOutOfRangeException(nameof(request), m.Role, "Unknown role.");
      }

      if (m.Role == Role.Assistant && m.ToolCalls is { Count: > 0 })
      {
        foreach (ToolCall call in m.ToolCalls)
        {
          items.Add(new Dictionary<string, object?>
          {
            ["type"] = "function_call",
            ["call_id"] = call.Id,
            ["name"] = call.Name,
            ["arguments"] = call.Arguments,
          });
        }
      }
    }

    return [.. items];
  }

  /// <summary>One message item's content: an input-part array when the message carries
  ///     parts (text → input_text, image → input_image with a data: URL), a single
  ///     input_text part otherwise. The message's own text content rides FIRST when
  ///     parts exist but none of them is a text part (issue #123): the typed body of
  ///     an image-attached user message lives in Content while Parts carries only the
  ///     images — emitting the parts alone would silently drop the text.</summary>
  private static object[] InputContent(Message m)
  {
    if (m.Parts is not { Count: > 0 })
    {
      return [TextPart(m.Content)];
    }

    object[] content = [.. m.Parts.Select(PartContent)];
    return m.Parts.Any(p => p is MessagePart.TextPart) || string.IsNullOrEmpty(m.Content)
        ? content
        : [TextPart(m.Content), .. content];
  }

  private static Dictionary<string, object?> MessageItem(string role, object[] content) => new()
  {
    ["type"] = "message",
    ["role"] = role,
    ["content"] = content,
  };

  private static Dictionary<string, object?> TextPart(string text) => new()
  {
    ["type"] = "input_text",
    ["text"] = text,
  };

  private static Dictionary<string, object?> PartContent(MessagePart part) => part switch
  {
    MessagePart.TextPart text => TextPart(text.Text),
    MessagePart.ImagePart image => new Dictionary<string, object?>
    {
      ["type"] = "input_image",
      ["image_url"] = $"data:{image.MediaType};base64,{image.Base64Data}",
    },
    _ => throw new InvalidOperationException(
          $"Unhandled message part type: {part.GetType().Name}."),
  };

  /// <summary>One tool result: a flat string output when the message carries only text
  ///     (the common case), an input-part array when it carries images — the text
  ///     content rides FIRST when parts exist but none is a text part (issue #123,
  ///     same shape as user messages: Agent.ToParts builds image-only parts) — strict
  ///     attribution: a well-formed history always carries the tool call id, a null
  ///     one is programmer error and coerces to nothing.</summary>
  private static Dictionary<string, object?> FunctionCallOutput(Message m)
  {
    if (string.IsNullOrEmpty(m.ToolCallId))
    {
      throw new ArgumentException(
          "A tool message must have a ToolCallId; the responses wire cannot attribute the result to a tool call.",
          nameof(m));
    }

    if (m.Parts is not { Count: > 0 } parts || !parts.Any(p => p is MessagePart.ImagePart))
    {
      return new Dictionary<string, object?>
      {
        ["type"] = "function_call_output",
        ["call_id"] = m.ToolCallId,
        ["output"] = m.Content,
      };
    }

    object[] content = [.. parts.Select(PartContent)];
    object[] output = parts.Any(p => p is MessagePart.TextPart) || string.IsNullOrEmpty(m.Content)
        ? content
        : [TextPart(m.Content), .. content];
    return new Dictionary<string, object?>
    {
      ["type"] = "function_call_output",
      ["call_id"] = m.ToolCallId,
      ["output"] = output,
    };
  }

  /// <summary>Translates a tool definition to the Responses API's FLAT function shape —
  ///     name and parameters at the top level, no nested "function" object as
  ///     chat-completions carried. The "items" schema key is emitted only for array
  ///     parameters; a null items key was tolerated on chat-completions but the
  ///     responses surface validates schemas strictly.</summary>
  public static object TranslateTool(ToolDefinition t)
  {
    ArgumentNullException.ThrowIfNull(t);
    return new Dictionary<string, object?>
    {
      ["type"] = "function",
      ["name"] = t.Name,
      ["description"] = t.Description,
      ["parameters"] = new Dictionary<string, object?>
      {
        ["type"] = "object",
        ["properties"] = t.Parameters.ToDictionary(
                p => p.Name,
                p =>
                {
                  Dictionary<string, object?> props = new()
                  {
                    ["type"] = p.Type switch
                    {
                      ToolParameterType.Text => "string",
                      ToolParameterType.TextArray => "array",
                      ToolParameterType.WholeNumber => "integer",
                      ToolParameterType.Flag => "boolean",
                      ToolParameterType.ObjectValue => "object",
                      ToolParameterType.ObjectArray => "array",
                      _ => throw new InvalidOperationException(
                            $"Unhandled tool parameter type: {p.Type}"),
                    },
                    ["description"] = p.Description,
                  };
                  if (ItemsType(p.Type) is { } itemsType)
                  {
                    props["items"] = new Dictionary<string, object?> { ["type"] = itemsType };
                  }

                  if (p.Minimum is { } min)
                  {
                    props["minimum"] = min;
                  }

                  return props;
                }),
        ["required"] = t.RequiredParameters.ToArray(),
        ["additionalProperties"] = false,
      },
    };
  }

  /// <summary>The 'items' schema type for an array parameter, or null when the type is
  ///     not an array. TextArray carries strings; ObjectArray carries objects whose
  ///     shape the tool's own parser validates.</summary>
  private static string? ItemsType(ToolParameterType type) =>
       type switch
       {
         ToolParameterType.Text => null,
         ToolParameterType.TextArray => "string",
         ToolParameterType.WholeNumber => null,
         ToolParameterType.Flag => null,
         ToolParameterType.ObjectValue => null,
         ToolParameterType.ObjectArray => "object",
         _ => null,
       };
}
