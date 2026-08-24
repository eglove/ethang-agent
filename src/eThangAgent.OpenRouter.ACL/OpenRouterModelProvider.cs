using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.OpenRouter.ACL;

public class OpenRouterModelProvider : IModelProvider
{
    private readonly HttpClient _http;
    private readonly OpenRouterConfiguration _config;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<double> _jitter;

    public OpenRouterModelProvider(HttpClient http, OpenRouterConfiguration config,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<double>? jitter = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        // Seams for deterministic tests; production uses Task.Delay with random 1x-2x spread.
        _delay = delay ?? ((span, token) => Task.Delay(span, token));
        _jitter = jitter ?? Random.Shared.NextDouble;
    }

    public async Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
    {
        var attempts = _config.Retry.MaxAttempts;
        for (var attempt = 1; ; attempt++)
        {
            var outcome = await SendOnceAsync(config, request, ct);
            if (!outcome.Retryable || ct.IsCancellationRequested || attempt >= attempts)
                return outcome.Result;
            if (!await BackoffAsync(attempt, outcome.RetryAfter))
                return outcome.Result; // cancelled while waiting — surface the last failure
        }
    }

    /// <summary>Sleeps the policy-computed backoff before the next retry. Returns false when
    ///     cancelled while waiting, so the caller surfaces the last failure instead of looping.</summary>
    private async Task<bool> BackoffAsync(int attempt, TimeSpan? retryAfter)
    {
        try
        {
            await _delay(_config.Retry.ComputeDelay(attempt, _jitter(), retryAfter), CancellationToken.None);
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
            using var httpRequest = CreateRequest(config, request, stream: false);
            using var response = await _http.SendAsync(httpRequest, ct);
            if (!response.IsSuccessStatusCode)
                return StatusOutcome((int)response.StatusCode, response.Headers.RetryAfter?.Delta);

            return AttemptOutcome.Final(await ReadJsonBodyAsync(response, ct));
        }
        catch (OperationCanceledException)
        {
            return new AttemptOutcome(Result<ModelResponse>.Failure(new Error("ProviderTimeout",
                "Request timed out.")), Retryable: true, RetryAfter: null);
        }
        catch (HttpRequestException ex)
        {
            return new AttemptOutcome(Result<ModelResponse>.Failure(new Error("ProviderError", ex.Message)),
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
        var attempts = _config.Retry.MaxAttempts;
        for (var attempt = 1; ; attempt++)
        {
            var emitted = false;
            Action<string>? contentSink = onContentDelta is null ? null : t => { emitted = true; onContentDelta(t); };
            Action<string>? reasoningSink = onReasoningDelta is null ? null : t => { emitted = true; onReasoningDelta(t); };

            var outcome = await SendStreamingOnceAsync(config, request, contentSink, reasoningSink, ct);
            // Once a delta has reached a callback it cannot be replayed without duplicating
            // output — mid-stream failures surface to the caller as errors, not retries.
            if (!outcome.Retryable || emitted || ct.IsCancellationRequested || attempt >= attempts)
                return outcome.Result;
            if (!await BackoffAsync(attempt, outcome.RetryAfter))
                return outcome.Result;
        }
    }

    private async Task<AttemptOutcome> SendStreamingOnceAsync(ModelConfig config, ModelRequest request,
        Action<string>? onContentDelta, Action<string>? onReasoningDelta, CancellationToken ct)
    {
        try
        {
            using var httpRequest = CreateRequest(config, request, stream: true);
            // Headers-read completion so the body surfaces incrementally instead of buffering.
            using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return StatusOutcome((int)response.StatusCode, response.Headers.RetryAfter?.Delta);

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType == "text/event-stream")
                return AttemptOutcome.Final(await ReadSseStreamAsync(response, onContentDelta, onReasoningDelta, ct));

            return AttemptOutcome.Final(await ReadJsonBodyAsync(response, ct));
        }
        catch (OperationCanceledException)
        {
            return new AttemptOutcome(Result<ModelResponse>.Failure(new Error("ProviderTimeout",
                "Request timed out.")), Retryable: true, RetryAfter: null);
        }
        catch (HttpRequestException ex)
        {
            return new AttemptOutcome(Result<ModelResponse>.Failure(new Error("ProviderError", ex.Message)),
                Retryable: true, RetryAfter: null);
        }
        catch (IOException ex)
        {
            return new AttemptOutcome(Result<ModelResponse>.Failure(new Error("ProviderError",
                $"Connection lost while reading the provider stream: {ex.Message}")),
                Retryable: true, RetryAfter: null);
        }
    }

    private HttpRequestMessage CreateRequest(ModelConfig config, ModelRequest request, bool stream)
    {
        var bodyDict = new Dictionary<string, object?>
        {
            ["model"] = config.ModelId,
            ["messages"] = BuildMessages(request),
            ["max_tokens"] = config.MaxTokens,
            ["temperature"] = config.Temperature,
        };
        if (stream)
            bodyDict["stream"] = true;
        if (request.Tools is { Count: > 0 })
            bodyDict["tools"] = request.Tools.Select(TranslateTool).ToArray();

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(_config.BaseUrl, "/api/v1/chat/completions"))
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
        var failure = statusCode switch
        {
            429 => Result<ModelResponse>.Failure(new Error("RateLimited",
                "OpenRouter rate limit exceeded.")),
            408 => Result<ModelResponse>.Failure(new Error("ProviderTimeout",
                "Request timed out.")),
            _ => Result<ModelResponse>.Failure(new Error("ProviderError",
                $"OpenRouter returned HTTP {statusCode}."))
        };
        return new AttemptOutcome(failure,
            Retryable: statusCode is 408 or 429 || statusCode >= 500,
            RetryAfter: retryAfter);
    }

    private static async Task<Result<ModelResponse>> ReadJsonBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return ParseChatCompletion(body);
        }
        catch (JsonException ex)
        {
            return Result<ModelResponse>.Failure(new Error("ProviderError",
                $"Invalid provider response: {ex.Message}"));
        }
        catch (KeyNotFoundException ex)
        {
            return Result<ModelResponse>.Failure(new Error("ProviderError",
                $"Malformed provider response: {ex.Message}"));
        }
        catch (InvalidOperationException ex)
        {
            return Result<ModelResponse>.Failure(new Error("ProviderError",
                $"Malformed provider response: {ex.Message}"));
        }
    }

    private static Result<ModelResponse> ParseChatCompletion(JsonElement body)
    {
        var choices = body.GetProperty("choices");
        if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            throw new InvalidOperationException("Provider response contains no choices.");
        var message = choices[0].GetProperty("message");
        var content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;

        var toolCalls = new List<ToolCallRequest>();
        if (message.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in tc.EnumerateArray())
            {
                var fn = call.GetProperty("function");
                toolCalls.Add(new ToolCallRequest(
                    call.GetProperty("id").GetString()!,
                    fn.GetProperty("name").GetString()!,
                    fn.GetProperty("arguments").GetString() ?? ""));
            }
        }

        return Result<ModelResponse>.Success(
            new ModelResponse(content, toolCalls, ParseFinishReason(choices[0])));
    }

    /// <summary>Translates OpenRouter's finish_reason vocabulary into the provider-neutral
    ///     enum. A missing value means the provider did not say, treated as Stop.</summary>
    private static FinishReason ParseFinishReason(JsonElement choice)
    {
        if (!choice.TryGetProperty("finish_reason", out var reason)
            || reason.ValueKind != JsonValueKind.String)
            return FinishReason.Stop;
        return reason.GetString() switch
        {
            "stop" => FinishReason.Stop,
            "length" => FinishReason.Length,
            "tool_calls" => FinishReason.ToolCalls,
            "content_filter" => FinishReason.ContentFilter,
            _ => FinishReason.Unknown,
        };
    }

    /// <summary>Consumes an SSE body: "data: {json}" frames carrying delta objects,":"-prefixed
    ///     keep-alive comments, and the "data: [DONE]" terminator. Content fragments stream
    ///     straight through; tool-call fragments accumulate per index until the stream ends.</summary>
    private static async Task<Result<ModelResponse>> ReadSseStreamAsync(HttpResponseMessage response,
        Action<string>? onContentDelta,
        Action<string>? onReasoningDelta,
        CancellationToken ct)
    {
        var content = new StringBuilder();
        var toolCalls = new Dictionary<int, StreamedToolCall>();
        var finishReason = FinishReason.Stop;
        var sawDone = false;
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.Length == 0 || line.StartsWith(':'))
                    continue; // event separator or keep-alive comment (": OPENROUTER PROCESSING")
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                    continue; // only data frames carry payload
                var payload = line["data:".Length..].Trim();
                if (payload == "[DONE]")
                {
                    sawDone = true;
                    break;
                }

                using var doc = JsonDocument.Parse(payload);
                if (ApplyChunk(doc.RootElement, content, toolCalls, onContentDelta, onReasoningDelta)
                    is { } chunkReason)
                    finishReason = chunkReason;
            }

            // A stream that ends without [DONE] was cut off (connection drop, proxy kill),
            // not completed. Failing loudly beats returning a silently truncated response;
            // non-retryable because deltas already streamed to the observer.
            if (!sawDone)
                return Result<ModelResponse>.Failure(new Error("StreamInterrupted",
                    "Provider stream ended without its [DONE] terminator."));

            // Assembled inside the guard: strict fragment validation (missing id/name) is a
            // provider-stream failure delivered as a Result, never an escaped exception.
            return Result<ModelResponse>.Success(new ModelResponse(
                content.Length > 0 ? content.ToString() : null,
                toolCalls.OrderBy(pair => pair.Key)
                    .Select(pair => pair.Value.ToRequest())
                    .ToList(),
                finishReason));
        }
        catch (JsonException ex)
        {
            return Result<ModelResponse>.Failure(new Error("ProviderError",
                $"Invalid provider stream: {ex.Message}"));
        }
        catch (InvalidOperationException ex)
        {
            return Result<ModelResponse>.Failure(new Error("ProviderError",
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
        if (!chunk.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
            return null; // usage-only or heartbeat frames carry no choices

        var choice = choices[0];
        if (!choice.TryGetProperty("delta", out var delta))
            return null;

        if (delta.TryGetProperty("content", out var contentDelta)
            && contentDelta.ValueKind == JsonValueKind.String)
        {
            var text = contentDelta.GetString()!;
            if (text.Length > 0)
            {
                content.Append(text);
                onContentDelta?.Invoke(text);
            }
            // else: structural no-op frame — carries no information, emits nothing.
        }

        if (delta.TryGetProperty("reasoning_content", out var reasoningContent)
            && reasoningContent.ValueKind == JsonValueKind.String)
        {
            var text = reasoningContent.GetString()!;
            if (text.Length > 0)
                onReasoningDelta?.Invoke(text);
        }
        else if (delta.TryGetProperty("reasoning", out var reasoning)
            && reasoning.ValueKind == JsonValueKind.String)
        {
            var text = reasoning.GetString()!;
            if (text.Length > 0)
                onReasoningDelta?.Invoke(text);
        }

        if (delta.TryGetProperty("tool_calls", out var calls)
            && calls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in calls.EnumerateArray())
            {
                var index = call.TryGetProperty("index", out var idx)
                    && idx.ValueKind == JsonValueKind.Number
                        ? idx.GetInt32()
                        : toolCalls.Count;
                if (!toolCalls.TryGetValue(index, out var fragment))
                    toolCalls[index] = fragment = new StreamedToolCall();
                if (call.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    fragment.Id = id.GetString();
                if (call.TryGetProperty("function", out var function))
                {
                    if (function.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                        fragment.Name = name.GetString();
                    if (function.TryGetProperty("arguments", out var arguments)
                        && arguments.ValueKind == JsonValueKind.String)
                        fragment.AppendArguments(arguments.GetString()!);
                }
            }
        }

        return ParseChunkFinishReason(choice);
    }

    /// <summary>Per-chunk translation of OpenRouter's finish_reason vocabulary. Missing on a
    ///     delta frame → null, so it never overwrites an already-seen reason.</summary>
    private static FinishReason? ParseChunkFinishReason(JsonElement choice)
    {
        if (!choice.TryGetProperty("finish_reason", out var reason)
            || reason.ValueKind != JsonValueKind.String)
            return null;
        return reason.GetString() switch
        {
            "stop" => FinishReason.Stop,
            "length" => FinishReason.Length,
            "tool_calls" => FinishReason.ToolCalls,
            "content_filter" => FinishReason.ContentFilter,
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
        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            messages.Add(new { role = "system", content = request.SystemPrompt });
        messages.AddRange(request.Messages.Select(TranslateMessage));
        return messages.ToArray();
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
                type = "function",
                function = new { name = t.Name, arguments = t.Arguments }
            }).ToArray()
        },
        Role.Assistant => new { role = "assistant", content = m.Content },
        Role.Tool => new { role = "tool", content = m.Content, tool_call_id = m.ToolCallId },
        _ => throw new ArgumentOutOfRangeException(nameof(m), m.Role, "Unknown role.")
    };

    private static object TranslateTool(ToolDefinition t) => new Dictionary<string, object?>
    {
        ["type"] = "function",
        ["function"] = new Dictionary<string, object?>
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
                        var props = new Dictionary<string, object?>
                        {
                            ["items"] = p.Type == ToolParameterType.StringArray
                                ? new Dictionary<string, object?> { ["type"] = "string" } : null,
                            ["type"] = p.Type switch
                            {
                                ToolParameterType.String => "string",
                                ToolParameterType.StringArray => "array",
                                ToolParameterType.Integer => "integer",
                                ToolParameterType.Boolean => "boolean",
                                _ => throw new InvalidOperationException(
                                    $"Unhandled tool parameter type: {p.Type}"),
                            },
                            ["description"] = p.Description,
                        };
                        if (p.Minimum is { } min) props["minimum"] = min;
                        return props;
                    }),
                ["required"] = t.RequiredParameters.ToArray(),
                ["additionalProperties"] = false,
            },
        }
    };
}
