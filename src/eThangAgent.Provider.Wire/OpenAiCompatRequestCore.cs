using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Provider.Wire;

/// <summary>Wire-level core for OpenAI-compatible request building and response parsing:
///     builds the messages/tools payload (<see cref="BuildMessages"/>,
///     <see cref="TranslateMessage"/>, <see cref="TranslateTool"/>) and parses a
///     chat-completions JSON body (<see cref="ParseChatCompletion"/>), with the retry
///     verdict record <see cref="AttemptOutcome"/> alongside. Shared deliberately: the
///     request/parse plumbing is byte-identical across OpenAI-compatible providers and
///     lives OUTSIDE the domain, so the ACLs-share-no-domain-code doctrine (AGENTS.md)
///     stays intact — Wire already references the domain contracts it translates (same
///     standing as <see cref="OpenAiCompatStreamCore"/>).</summary>
public static class OpenAiCompatRequestCore
{
  private const string ToolCalls = "tool_calls";
  private const string Function = "function";

  public static Result<ModelResponse> ParseChatCompletion(JsonElement body)
  {
    JsonElement choices = body.GetProperty("choices");
    if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
    {
      throw new InvalidOperationException("Provider response contains no choices.");
    }

    JsonElement message = choices[0].GetProperty("message");
    string? content = message.TryGetProperty("content", out JsonElement c) && c.ValueKind == JsonValueKind.String
        ? c.GetString()
        : null;

    List<ToolCallRequest> toolCalls = [];
    if (message.TryGetProperty(ToolCalls, out JsonElement tc) && tc.ValueKind == JsonValueKind.Array)
    {
      foreach (JsonElement call in tc.EnumerateArray())
      {
        JsonElement fn = call.GetProperty(Function);
        toolCalls.Add(new ToolCallRequest(
            call.GetProperty("id").GetString()!,
            fn.GetProperty("name").GetString()!,
            fn.GetProperty("arguments").GetString() ?? ""));
      }
    }

    return Result.Success(
        new ModelResponse(content, toolCalls, ParseFinishReason(choices[0]), ParseUsage(body)));
  }

  /// <summary>Maps the OpenAI-compatible usage object (prompt_tokens / completion_tokens /
  ///     prompt_tokens_details.cached_tokens) into TokenUsage; null when absent. Serves the
  ///     non-streaming JSON fallback path; streaming parses through the shared wire core.</summary>
  public static TokenUsage? ParseUsage(JsonElement parent)
  {
    if (!parent.TryGetProperty("usage", out JsonElement u) || u.ValueKind != JsonValueKind.Object)
    {
      return null;
    }

    if (!TryGetInt(u, "prompt_tokens", out int prompt) || !TryGetInt(u, "completion_tokens", out int completion))
    {
      return null;
    }

    int? cached = null;
    if (u.TryGetProperty("prompt_tokens_details", out JsonElement details)
        && details.ValueKind == JsonValueKind.Object
        && TryGetInt(details, "cached_tokens", out int cachedValue))
    {
      cached = cachedValue;
    }

    return new TokenUsage(prompt, completion, cached);
  }

  public static bool TryGetInt(JsonElement parent, string name, out int value)
  {
    value = 0;
    return parent.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value);
  }

  /// <summary>Translates the provider's finish_reason vocabulary into the provider-neutral enum.
  ///     A missing value means the provider did not say, treated as Stop.</summary>
  private static FinishReason ParseFinishReason(JsonElement choice)
  {
    return !choice.TryGetProperty("finish_reason", out JsonElement reason)
        || reason.ValueKind != JsonValueKind.String
      ? FinishReason.Stop
      : reason.GetString() switch
      {
        "stop" => FinishReason.Stop,
        "length" => FinishReason.Length,
        ToolCalls => FinishReason.ToolCalls,
        "sensitive" => FinishReason.ContentFilter,
        // Input exceeded the model's context window — closest actionable meaning is Length.
        "model_context_window_exceeded" => FinishReason.Length,
        _ => FinishReason.Unknown,
      };
  }

  /// <summary>One provider attempt's verdict plus what a retry decision needs: whether the
  ///     failure was transient and any server-provided Retry-After hint.</summary>
  // Named decision (CA1034): the record nests inside the core it annotates by pinned design —
  // it travels with the core to every provider ACL, one name for the whole OpenAI-compatible family.
#pragma warning disable CA1034 // Do not nest type
  public sealed record AttemptOutcome(
      Result<ModelResponse> Result,
      bool Retryable,
      TimeSpan? RetryAfter)
  {
    public static AttemptOutcome Final(Result<ModelResponse> result) =>
        new(result, Retryable: false, RetryAfter: null);
  }
#pragma warning restore CA1034 // Do not nest type

  public static object[] BuildMessages(ModelRequest request)
  {
    ArgumentNullException.ThrowIfNull(request);
    List<object> messages = [];
    if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
    {
      messages.Add(new { role = "system", content = request.SystemPrompt });
    }

    messages.AddRange(request.Messages.Select(TranslateMessage));
    return [.. messages];
  }

  public static object TranslateMessage(Message m) => (m ?? throw new ArgumentNullException(nameof(m))).Role switch
  {
    Role.System => new { role = "system", content = m.Content },
    Role.User => new { role = "user", content = m.Content },
    Role.Assistant when m.ToolCalls is { Count: > 0 } => new
    {
      role = "assistant",
      content = m.Content,
      tool_calls = m.ToolCalls.Select(t => new
      {
        id = t.Id,
        type = Function,
        function = new { name = t.Name, arguments = t.Arguments }
      }).ToArray()
    },
    Role.Assistant => new { role = "assistant", content = m.Content },
    Role.Tool => new { role = "tool", content = m.Content, tool_call_id = m.ToolCallId },
    _ => throw new ArgumentOutOfRangeException(nameof(m), m.Role, "Unknown role.")
  };

  public static object TranslateTool(ToolDefinition t)
  {
    ArgumentNullException.ThrowIfNull(t);
    return new Dictionary<string, object?>
    {
      ["type"] = Function,
      [Function] = new Dictionary<string, object?>
      {
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
                      ["items"] = p.Type == ToolParameterType.TextArray
                              ? new Dictionary<string, object?> { ["type"] = "string" } : null,
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
                    if (p.Minimum is { } min)
                    {
                      props["minimum"] = min;
                    }

                    return props;
                  }),
          ["required"] = t.RequiredParameters.ToArray(),
          ["additionalProperties"] = false,
        },
      }
    };
  }
}
