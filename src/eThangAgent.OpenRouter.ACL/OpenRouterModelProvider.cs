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

    public OpenRouterModelProvider(HttpClient http, OpenRouterConfiguration config)
    {
        _http = http;
        _config = config;
    }

    public async Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
    {
        try
        {
            using var httpRequest = CreateRequest(config, request, stream: false);
            using var response = await _http.SendAsync(httpRequest, ct);
            if (!response.IsSuccessStatusCode)
                return StatusFailure((int)response.StatusCode);

            return await ReadJsonBodyAsync(response, ct);
        }
        catch (TaskCanceledException)
        {
            return Result<ModelResponse>.Failure(new Error("ProviderTimeout",
                "Request timed out."));
        }
        catch (HttpRequestException ex)
        {
            return Result<ModelResponse>.Failure(new Error("ProviderError", ex.Message));
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
        try
        {
            using var httpRequest = CreateRequest(config, request, stream: true);
            // Headers-read completion so the body surfaces incrementally instead of buffering.
            using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return StatusFailure((int)response.StatusCode);

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType == "text/event-stream")
                return await ReadSseStreamAsync(response, onContentDelta, onReasoningDelta, ct);

            return await ReadJsonBodyAsync(response, ct);
        }
        catch (OperationCanceledException)
        {
            return Result<ModelResponse>.Failure(new Error("ProviderTimeout",
                "Request timed out."));
        }
        catch (HttpRequestException ex)
        {
            return Result<ModelResponse>.Failure(new Error("ProviderError", ex.Message));
        }
        catch (IOException ex)
        {
            return Result<ModelResponse>.Failure(new Error("ProviderError",
                $"Connection lost while reading the provider stream: {ex.Message}"));
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

    private static Result<ModelResponse> StatusFailure(int statusCode) => statusCode switch
    {
        429 => Result<ModelResponse>.Failure(new Error("RateLimited",
            "OpenRouter rate limit exceeded.")),
        408 => Result<ModelResponse>.Failure(new Error("ProviderTimeout",
            "Request timed out.")),
        _ => Result<ModelResponse>.Failure(new Error("ProviderError",
            $"OpenRouter returned HTTP {statusCode}."))
    };

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

        return Result<ModelResponse>.Success(new ModelResponse(content, toolCalls));
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
                    break;

                using var doc = JsonDocument.Parse(payload);
                ApplyChunk(doc.RootElement, content, toolCalls, onContentDelta, onReasoningDelta);
            }

            // Assembled inside the guard: strict fragment validation (missing id/name) is a
            // provider-stream failure delivered as a Result, never an escaped exception.
            return Result<ModelResponse>.Success(new ModelResponse(
                content.Length > 0 ? content.ToString() : null,
                toolCalls.OrderBy(pair => pair.Key)
                    .Select(pair => pair.Value.ToRequest())
                    .ToList()));
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

    private static void ApplyChunk(JsonElement chunk, StringBuilder content,
        Dictionary<int, StreamedToolCall> toolCalls,
        Action<string>? onContentDelta,
        Action<string>? onReasoningDelta)
    {
        if (!chunk.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
            return; // usage-only or heartbeat frames carry no choices

        var choice = choices[0];
        if (!choice.TryGetProperty("delta", out var delta))
            return;

        if (delta.TryGetProperty("content", out var contentDelta)
            && contentDelta.ValueKind == JsonValueKind.String)
        {
            var text = contentDelta.GetString()!;
            content.Append(text);
            onContentDelta?.Invoke(text);
        }

        if (delta.TryGetProperty("reasoning_content", out var reasoningDelta)
            && reasoningDelta.ValueKind == JsonValueKind.String)
        {
            onReasoningDelta?.Invoke(reasoningDelta.GetString()!);
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
                            ["type"] = p.Type switch
                            {
                                ToolParameterType.String => "string",
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
                ["required"] = t.Parameters.Select(p => p.Name).ToArray(),
                ["additionalProperties"] = false,
            },
        }
    };
}
