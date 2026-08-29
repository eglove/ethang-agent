using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Zai.ACL;

/// <summary>Sends domain chat requests to z.ai's OpenAI-compatible chat completions endpoint.
///     z.ai is a single provider, so <see cref="ModelConfig.Provider"/> (an OpenRouter upstream
///     routing pin) has no meaning here and is never serialized. The <c>thinking</c> knob is
///     deliberately never sent: GLM defaults apply (flagship models force thinking on) and
///     reasoning surfaces through the standard <c>reasoning_content</c> stream field.
///     <see cref="ModelConfig.Effort"/> — set by the user via the host's effort picker —
///     maps to <c>reasoning_effort</c> when present. Temperature passes through unvalidated — z.ai
///     rejects out-of-range values server-side (HTTP 400 → ProviderError) rather than this ACL
///     clamping silently.</summary>
public sealed class ZaiModelProvider(HttpClient http, ZaiConfiguration config,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    Func<double>? jitter = null) : IModelProvider
{
  private const string ProviderError = "ProviderError";
  private const string ToolCalls = "tool_calls";
  private const string Function = "function";

  private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
  private readonly ZaiConfiguration _config = config ?? throw new ArgumentNullException(nameof(config));
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
  /// <paramref name="onContentDelta"/> and reasoning fragments through
  /// <paramref name="onReasoningDelta"/> as they arrive, assembles tool-call fragments by
  /// index, and returns the fully assembled final response — the value SendAsync would
  /// produce for the same request. When the server ignores the stream flag and answers a
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
    }

    // Only sent when the user picked a level (the effort picker); GLM defaults apply otherwise.
    if (config.Effort is { } effort)
    {
      bodyDict["reasoning_effort"] = ZaiReasoningEffort.ToWire(effort);
    }

    if (request.Tools is { Count: > 0 })
    {
      bodyDict["tools"] = request.Tools.Select(TranslateTool).ToArray();
    }

    HttpRequestMessage httpRequest = new(HttpMethod.Post, _config.ChatCompletionsEndpoint())
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
          "z.ai rate limit exceeded.")),
      408 => Result.Failure<ModelResponse>(new DomainError("ProviderTimeout",
          "Request timed out.")),
      _ => Result.Failure<ModelResponse>(new DomainError(ProviderError,
          $"z.ai returned HTTP {statusCode}."))
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
        new ModelResponse(content, toolCalls, ParseFinishReason(choices[0])));
  }

  /// <summary>Translates z.ai's finish_reason vocabulary into the provider-neutral enum.
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

  /// <summary>Consumes an SSE body: "data: {json}" frames carrying delta objects, ":"-prefixed
  ///     keep-alive comments, and the "data: [DONE]" terminator. Content and reasoning
  ///     fragments stream straight through; tool-call fragments accumulate per index until
  ///     the stream ends.</summary>
  private static async Task<Result<ModelResponse>> ReadSseStreamAsync(HttpResponseMessage response,
      Action<string>? onContentDelta,
      Action<string>? onReasoningDelta,
      CancellationToken ct)
  {
    StringBuilder content = new();
    Dictionary<int, StreamedToolCall> toolCalls = [];
    FinishReason finishReason = FinishReason.Stop;
    bool sawDone = false;
    try
    {
      using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
      using StreamReader reader = new(stream);
      while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
      {
        if (line.Length == 0 || line.StartsWith(':'))
        {
          continue; // event separator or keep-alive comment
        }

        if (!line.StartsWith("data:", StringComparison.Ordinal))
        {
          continue; // only data frames carry payload
        }

        string payload = line["data:".Length..].Trim();
        if (payload == "[DONE]")
        {
          sawDone = true;
          break;
        }

        using JsonDocument doc = JsonDocument.Parse(payload);
        if (ApplyChunk(doc.RootElement, content, toolCalls, onContentDelta, onReasoningDelta)
            is { } chunkReason)
        {
          finishReason = chunkReason;
        }
      }

      // A stream that ends without [DONE] was cut off (connection drop, proxy kill),
      // not completed. Failing loudly beats returning a silently truncated response;
      // non-retryable because deltas already streamed to the observer.
      if (!sawDone)
      {
        return Result.Failure<ModelResponse>(new DomainError("StreamInterrupted",
            "Provider stream ended without its [DONE] terminator."));
      }

      // Assembled inside the guard: strict fragment validation (missing id/name) is a
      // provider-stream failure delivered as a Result, never an escaped exception.
      return Result.Success(new ModelResponse(
          content.Length > 0 ? content.ToString() : null,
          [.. toolCalls.OrderBy(pair => pair.Key).Select(pair => pair.Value.ToRequest())],
          finishReason));
    }
    catch (JsonException ex)
    {
      return Result.Failure<ModelResponse>(new DomainError(ProviderError,
          $"Invalid provider stream: {ex.Message}"));
    }
    catch (InvalidOperationException ex)
    {
      return Result.Failure<ModelResponse>(new DomainError(ProviderError,
          $"Malformed provider stream: {ex.Message}"));
    }
  }

  /// <summary>Applies one SSE chunk and returns the chunk's finish_reason when it
  ///     carries one, else null (delta/usage frames).</summary>
  private static FinishReason? ApplyChunk(JsonElement chunk, StringBuilder content,
      Dictionary<int, StreamedToolCall> toolCalls,
      Action<string>? onContentDelta,
      Action<string>? onReasoningDelta)
  {
    if (!chunk.TryGetProperty("choices", out JsonElement choices)
        || choices.ValueKind != JsonValueKind.Array
        || choices.GetArrayLength() == 0)
    {
      return null; // usage-only or heartbeat frames carry no choices
    }

    JsonElement choice = choices[0];
    if (!choice.TryGetProperty("delta", out JsonElement delta))
    {
      return null;
    }

    ApplyContentDelta(delta, content, onContentDelta);
    ApplyReasoningContent(delta, onReasoningDelta);
    ApplyToolCallFragments(delta, toolCalls);
    FinishReason? reason = ParseChunkFinishReason(choice);
    return reason;
  }

  /// <summary>Streams one content fragment: appended to the assembled response and
  ///     forwarded to the observer. Structural no-op frames carry no information and
  ///     emit nothing.</summary>
  private static void ApplyContentDelta(JsonElement delta, StringBuilder content, Action<string>? onContentDelta)
  {
    if (delta.TryGetProperty("content", out JsonElement contentDelta)
        && contentDelta.ValueKind == JsonValueKind.String)
    {
      string text = contentDelta.GetString()!;
      if (text.Length > 0)
      {
        _ = content.Append(text);
        onContentDelta?.Invoke(text);
      }
    }
  }

  /// <summary>Streams one reasoning fragment. GLM reasoning flows through
  ///     <c>reasoning_content</c> (z.ai's documented field).</summary>
  private static void ApplyReasoningContent(JsonElement delta, Action<string>? onReasoningDelta)
  {
    if (delta.TryGetProperty("reasoning_content", out JsonElement reasoning)
        && reasoning.ValueKind == JsonValueKind.String)
    {
      string text = reasoning.GetString()!;
      if (text.Length > 0)
      {
        onReasoningDelta?.Invoke(text);
      }
    }
  }

  private static void ApplyToolCallFragments(JsonElement delta, Dictionary<int, StreamedToolCall> toolCalls)
  {
    if (delta.TryGetProperty(ToolCalls, out JsonElement calls)
        && calls.ValueKind == JsonValueKind.Array)
    {
      foreach (JsonElement call in calls.EnumerateArray())
      {
        ApplyToolCallFragment(call, toolCalls);
      }
    }
  }

  /// <summary>Assembles one tool-call fragment: addressed by index, created on first
  ///     sight, with id/name/argument text merged into it.</summary>
  private static void ApplyToolCallFragment(JsonElement call, Dictionary<int, StreamedToolCall> toolCalls)
  {
    int index = call.TryGetProperty("index", out JsonElement idx)
        && idx.ValueKind == JsonValueKind.Number
            ? idx.GetInt32()
            : toolCalls.Count;
    if (!toolCalls.TryGetValue(index, out StreamedToolCall? fragment))
    {
      toolCalls[index] = fragment = new StreamedToolCall();
    }

    if (call.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String)
    {
      fragment.Id = id.GetString();
    }

    ApplyFunctionFragment(call, fragment);
  }

  private static void ApplyFunctionFragment(JsonElement call, StreamedToolCall fragment)
  {
    if (!call.TryGetProperty(Function, out JsonElement function))
    {
      return;
    }

    if (function.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String)
    {
      fragment.Name = name.GetString();
    }

    if (function.TryGetProperty("arguments", out JsonElement arguments)
        && arguments.ValueKind == JsonValueKind.String)
    {
      fragment.AppendArguments(arguments.GetString()!);
    }
  }

  /// <summary>Per-chunk translation of z.ai's finish_reason vocabulary. Missing on a
  ///     delta frame → null, so it never overwrites an already-seen reason.</summary>
  private static FinishReason? ParseChunkFinishReason(JsonElement choice)
  {
    return !choice.TryGetProperty("finish_reason", out JsonElement reason)
        || reason.ValueKind != JsonValueKind.String
      ? null
      : reason.GetString() switch
      {
        "stop" => FinishReason.Stop,
        "length" => FinishReason.Length,
        ToolCalls => FinishReason.ToolCalls,
        "sensitive" => FinishReason.ContentFilter,
        "model_context_window_exceeded" => FinishReason.Length,
        _ => FinishReason.Unknown,
      };
  }

  /// <summary>Accumulates one streamed tool call: id/name arrive on the first fragment,
  ///     argument text concatenates across every fragment for that index.</summary>
  private sealed class StreamedToolCall
  {
    public string? Id { get; set; }
    public string? Name { get; set; }

    private readonly StringBuilder _arguments = new();

    public void AppendArguments(string fragment) => _arguments.Append(fragment);

    public ToolCallRequest ToRequest() => new(
        Id ?? throw new InvalidOperationException("Streamed tool call carried no id."),
        Name ?? throw new InvalidOperationException(
            $"Streamed tool call '{Id}' carried no function name."),
        _arguments.ToString());
  }

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
    Role.System => new { role = "system", content = MessageTimestamp.Stamp(m) },
    Role.User => new { role = "user", content = MessageTimestamp.Stamp(m) },
    Role.Assistant when m.ToolCalls is { Count: > 0 } => new
    {
      role = "assistant",
      content = MessageTimestamp.Stamp(m),
      tool_calls = m.ToolCalls.Select(t => new
      {
        id = t.Id,
        type = Function,
        function = new { name = t.Name, arguments = t.Arguments }
      }).ToArray()
    },
    Role.Assistant => new { role = "assistant", content = MessageTimestamp.Stamp(m) },
    Role.Tool => new { role = "tool", content = MessageTimestamp.Stamp(m), tool_call_id = m.ToolCallId },
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
