using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.Provider.Wire;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.OpenRouter.ACL;

public class OpenRouterModelProvider(HttpClient http, OpenRouterConfiguration config,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    Func<double>? jitter = null) : IModelProvider
{
  private const string ProviderError = "ProviderError";
  private const string ToolCalls = "tool_calls";
  private const string Function = "function";

  private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
  private readonly OpenRouterConfiguration _config = config ?? throw new ArgumentNullException(nameof(config));
  private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((span, token) => Task.Delay(span, token));
  private readonly Func<double> _jitter = jitter ?? Random.Shared.NextDouble;

  public async Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
  {
    int attempts = _config.Retry.MaxAttempts;
    for (int attempt = 1; attempt <= attempts; attempt++)
    {
      AttemptOutcome outcome = await SendOnceAsync(config, request, ct).ConfigureAwait(false);
      if (!outcome.Retryable || ct.IsCancellationRequested || attempt == attempts)
      {
        return outcome.Result;
      }

      if (!await BackoffAsync(attempt, outcome.RetryAfter).ConfigureAwait(false))
      {
        return outcome.Result; // cancelled while waiting — surface the last failure
      }
    }

    // Dead code: RetryPolicy validates MaxAttempts >= 1, so the loop always runs.
    throw new UnreachableException();
  }

  /// <summary>Sleeps the policy-computed backoff before the next retry. Returns false when
  ///     cancelled while waiting, so the caller surfaces the last failure instead of looping.</summary>
  private async Task<bool> BackoffAsync(int attempt, TimeSpan? retryAfter)
  {
    try
    {
      await _delay(_config.Retry.ComputeDelay(attempt, _jitter(), retryAfter), CancellationToken.None).ConfigureAwait(false);
      return true;
    }
    catch (OperationCanceledException)
    {
      return false;
    }
  }

  private async Task<AttemptOutcome> SendOnceAsync(ModelConfig config, ModelRequest request, CancellationToken ct)
  {
    try
    {
      using HttpRequestMessage httpRequest = CreateRequest(config, request, stream: false);
      using HttpResponseMessage response = await _http.SendAsync(httpRequest, ct).ConfigureAwait(false);
      return !response.IsSuccessStatusCode
        ? StatusOutcome((int)response.StatusCode, response.Headers.RetryAfter?.Delta)
        : AttemptOutcome.Final(await ReadJsonBodyAsync(response, ct).ConfigureAwait(false));
    }
    catch (OperationCanceledException)
    {
      return new AttemptOutcome(Result.Failure<ModelResponse>(new DomainError("ProviderTimeout",
          "Request timed out.")), Retryable: true, RetryAfter: null);
    }
    catch (HttpRequestException ex)
    {
      return new AttemptOutcome(Result.Failure<ModelResponse>(new DomainError(ProviderError, ex.Message)),
          Retryable: true, RetryAfter: null);
    }
  }

  /// <summary>
  /// Streams a completion over Server-Sent Events: emits every content fragment through
  /// <paramref name="onContentDelta"/> as it arrives, assembles tool-call fragments by
  /// index, and returns the fully assembled final response — the value SendAsync would
  /// produce for the same request. When a server ignores the stream flag and answers a
  /// single JSON document, that body is parsed exactly as SendAsync parses it: a transport
  /// fallback, never a change in parsing rules.
  /// </summary>
  public async Task<Result<ModelResponse>> SendStreamingAsync(ModelConfig config, ModelRequest request,
      Action<string>? onContentDelta = null,
      Action<string>? onReasoningDelta = null,
      CancellationToken ct = default)
  {
    int attempts = _config.Retry.MaxAttempts;
    for (int attempt = 1; attempt <= attempts; attempt++)
    {
      bool emitted = false;
      Action<string>? contentSink = onContentDelta is null ? null : t =>
      {
        emitted = true;
        onContentDelta(t);
      };
      Action<string>? reasoningSink = onReasoningDelta is null ? null : t =>
      {
        emitted = true;
        onReasoningDelta(t);
      };

      AttemptOutcome outcome = await SendStreamingOnceAsync(config, request, contentSink, reasoningSink, ct).ConfigureAwait(false);
      // Once a delta has reached a callback it cannot be replayed without duplicating
      // output — mid-stream failures surface to the caller as errors, not retries.
      if (!outcome.Retryable || emitted || ct.IsCancellationRequested || attempt == attempts)
      {
        return outcome.Result;
      }

      if (!await BackoffAsync(attempt, outcome.RetryAfter).ConfigureAwait(false))
      {
        return outcome.Result;
      }
    }

    // Dead code: RetryPolicy validates MaxAttempts >= 1, so the loop always runs.
    throw new UnreachableException();
  }

  private async Task<AttemptOutcome> SendStreamingOnceAsync(ModelConfig config, ModelRequest request,
      Action<string>? onContentDelta, Action<string>? onReasoningDelta, CancellationToken ct)
  {
    try
    {
      using HttpRequestMessage httpRequest = CreateRequest(config, request, stream: true);
      // Headers-read completion so the body surfaces incrementally instead of buffering.
      using HttpResponseMessage response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
      if (!response.IsSuccessStatusCode)
      {
        return StatusOutcome((int)response.StatusCode, response.Headers.RetryAfter?.Delta);
      }

      string? contentType = response.Content.Headers.ContentType?.MediaType;
      return contentType == "text/event-stream"
        ? AttemptOutcome.Final(await ReadSseStreamAsync(response, onContentDelta, onReasoningDelta, ct).ConfigureAwait(false))
        : AttemptOutcome.Final(await ReadJsonBodyAsync(response, ct).ConfigureAwait(false));
    }
    catch (OperationCanceledException)
    {
      return new AttemptOutcome(Result.Failure<ModelResponse>(new DomainError("ProviderTimeout",
          "Request timed out.")), Retryable: true, RetryAfter: null);
    }
    catch (HttpRequestException ex)
    {
      return new AttemptOutcome(Result.Failure<ModelResponse>(new DomainError(ProviderError, ex.Message)),
          Retryable: true, RetryAfter: null);
    }
    catch (IOException ex)
    {
      return new AttemptOutcome(Result.Failure<ModelResponse>(new DomainError(ProviderError,
          $"Connection lost while reading the provider stream: {ex.Message}")),
          Retryable: true, RetryAfter: null);
    }
  }

  private HttpRequestMessage CreateRequest(ModelConfig config, ModelRequest request, bool stream)
  {
    Dictionary<string, object?> bodyDict = new()
    {
      ["model"] = config.ModelId,
      ["messages"] = BuildMessages(request),
      ["max_tokens"] = config.MaxTokens,
      ["temperature"] = config.Temperature,
    };
    if (stream)
    {
      bodyDict["stream"] = true;
      // Ask the wire to send the final usage frame: accounting needs token counts.
      bodyDict["stream_options"] = new { include_usage = true };
    }

    if (!string.IsNullOrWhiteSpace(config.Provider))
    {
      bodyDict["provider"] = new { only = new[] { config.Provider } };
    }

    // Only sent when the user picked a level (the effort picker); OpenRouter's own
    // default applies otherwise, and it normalizes the level to what the model supports.
    if (config.Effort is { } effort)
    {
      bodyDict["reasoning"] = new { effort = OpenRouterReasoningEffort.ToWire(effort) };
    }

    if (request.Tools is { Count: > 0 })
    {
      bodyDict["tools"] = request.Tools.Select(TranslateTool).ToArray();
    }

    HttpRequestMessage httpRequest = new(HttpMethod.Post, _config.Endpoint("/api/v1/chat/completions"))
    {
      Content = JsonContent.Create(bodyDict)
    };
    httpRequest.Headers.Add("Authorization", $"Bearer {_config.ApiKey}");
    return httpRequest;
  }

  /// <summary>Maps an HTTP status to its error result plus retry classification: 408, 429,
  ///     and any 5xx are transient; everything else is permanent and fails immediately.</summary>
  private static AttemptOutcome StatusOutcome(int statusCode, TimeSpan? retryAfter)
  {
    Result<ModelResponse> failure = statusCode switch
    {
      429 => Result.Failure<ModelResponse>(new DomainError("RateLimited",
          "OpenRouter rate limit exceeded.")),
      408 => Result.Failure<ModelResponse>(new DomainError("ProviderTimeout",
          "Request timed out.")),
      _ => Result.Failure<ModelResponse>(new DomainError(ProviderError,
          $"OpenRouter returned HTTP {statusCode}."))
    };
    return new AttemptOutcome(failure,
        Retryable: statusCode is 408 or 429 or >= 500,
        RetryAfter: retryAfter);
  }

  private static async Task<Result<ModelResponse>> ReadJsonBodyAsync(HttpResponseMessage response, CancellationToken ct)
  {
    try
    {
      JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
      return ParseChatCompletion(body);
    }
    catch (JsonException ex)
    {
      return Result.Failure<ModelResponse>(new DomainError(ProviderError,
          $"Invalid provider response: {ex.Message}"));
    }
    catch (KeyNotFoundException ex)
    {
      return Result.Failure<ModelResponse>(new DomainError(ProviderError,
          $"Malformed provider response: {ex.Message}"));
    }
    catch (InvalidOperationException ex)
    {
      return Result.Failure<ModelResponse>(new DomainError(ProviderError,
          $"Malformed provider response: {ex.Message}"));
    }
  }

  private static Result<ModelResponse> ParseChatCompletion(JsonElement body)
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
  ///     prompt_tokens_details.cached_tokens) into TokenUsage; null when absent.</summary>
  private static TokenUsage? ParseUsage(JsonElement parent)
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

  private static bool TryGetInt(JsonElement parent, string name, out int value)
  {
    value = 0;
    return parent.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value);
  }

  /// <summary>Translates OpenRouter's finish_reason vocabulary into the provider-neutral
  ///     enum. A missing value means the provider did not say, treated as Stop.</summary>
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
        "content_filter" => FinishReason.ContentFilter,
        _ => FinishReason.Unknown,
      };
  }

  /// <summary>Streams the response body through the shared OpenAI-compatible stream
  ///     core, supplying OpenRouter's vocabulary (see <see cref="OpenRouterStreamVocabulary"/>).</summary>
  private static Task<Result<ModelResponse>> ReadSseStreamAsync(HttpResponseMessage response,
      Action<string>? onContentDelta,
      Action<string>? onReasoningDelta,
      CancellationToken ct)
    => OpenAiCompatStreamCore.ReadSseStreamAsync(response, OpenRouterStreamVocabulary.Instance,
        onContentDelta, onReasoningDelta, ct);

  /// <summary>One provider attempt's verdict plus what a retry decision needs: whether the
  ///     failure was transient and any server-provided Retry-After hint.</summary>
  private sealed record AttemptOutcome(
      Result<ModelResponse> Result,
      bool Retryable,
      TimeSpan? RetryAfter)
  {
    public static AttemptOutcome Final(Result<ModelResponse> result) =>
        new(result, Retryable: false, RetryAfter: null);
  }

  private static object[] BuildMessages(ModelRequest request)
  {
    List<object> messages = [];
    if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
    {
      messages.Add(new { role = "system", content = request.SystemPrompt });
    }

    messages.AddRange(request.Messages.Select(TranslateMessage));
    return [.. messages];
  }

  private static object TranslateMessage(Message m) => m.Role switch
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

  private static object TranslateTool(ToolDefinition t) => new Dictionary<string, object?>
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
