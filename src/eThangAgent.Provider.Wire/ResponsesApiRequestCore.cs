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
    List<ToolCallRequest> toolCalls = [];
    foreach (JsonElement item in output.EnumerateArray())
    {
      string type = item.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String
          ? t.GetString()! : "";
      switch (type)
      {
        case "message":
          AppendMessageContent(item, content);
          break;
        case "function_call":
          toolCalls.Add(new ToolCallRequest(
              item.GetProperty("call_id").GetString()!,
              item.GetProperty("name").GetString()!,
              item.TryGetProperty("arguments", out JsonElement args) && args.ValueKind == JsonValueKind.String
                  ? args.GetString() ?? "" : ""));
          break;
        default:
          break;
      }
    }

    return Result.Success(new ModelResponse(
        content.Length > 0 ? content.ToString() : null,
        toolCalls,
        ParseFinishReason(body, toolCalls.Count > 0),
        ParseUsage(body)));
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

  /// <summary>Maps the Responses-API usage object (input_tokens / output_tokens /
  ///     input_tokens_details.cached_tokens / server_tool_use.web_search_requests)
  ///     into TokenUsage; null when absent or missing its primary counts.</summary>
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

    int? cached = null;
    if (u.TryGetProperty("input_tokens_details", out JsonElement details)
        && details.ValueKind == JsonValueKind.Object
        && TryGetInt(details, "cached_tokens", out int cachedValue))
    {
      cached = cachedValue;
    }

    int? serverToolCalls = null;
    if (u.TryGetProperty("server_tool_use", out JsonElement serverUse)
        && serverUse.ValueKind == JsonValueKind.Object
        && TryGetInt(serverUse, "web_search_requests", out int webSearchRequests))
    {
      serverToolCalls = webSearchRequests;
    }

    return new TokenUsage(input, output, cached, serverToolCalls);
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
  ///     input_text part otherwise.</summary>
  private static object[] InputContent(Message m) =>
      m.Parts is { Count: > 0 }
          ? [.. m.Parts.Select(PartContent)]
          : [TextPart(m.Content)];

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
  ///     (the common case), an input-part array when it carries images — strict
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

    bool hasImages = m.Parts is { Count: > 0 } && m.Parts.Any(p => p is MessagePart.ImagePart);
    object output = hasImages
        ? m.Parts!.Select(PartContent).ToArray()
        : m.Content;
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
                      _ => throw new InvalidOperationException(
                            $"Unhandled tool parameter type: {p.Type}"),
                    },
                    ["description"] = p.Description,
                  };
                  if (p.Type == ToolParameterType.TextArray)
                  {
                    props["items"] = new Dictionary<string, object?> { ["type"] = "string" };
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
}
