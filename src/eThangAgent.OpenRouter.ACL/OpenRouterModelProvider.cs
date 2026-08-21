using System.Net.Http.Json;
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
            var bodyDict = new Dictionary<string, object?>
            {
                ["model"] = config.ModelId,
                ["messages"] = BuildMessages(request),
                ["max_tokens"] = config.MaxTokens,
                ["temperature"] = config.Temperature,
            };
            if (request.Tools is { Count: > 0 })
                bodyDict["tools"] = request.Tools.Select(TranslateTool).ToArray();

            var requestUri = new Uri(_config.BaseUrl, "/api/v1/chat/completions");
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = JsonContent.Create(bodyDict)
            };
            httpRequest.Headers.Add("Authorization", $"Bearer {_config.ApiKey}");

            using var response = await _http.SendAsync(httpRequest, ct);

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                return statusCode switch
                {
                    429 => Result<ModelResponse>.Failure(new Error("RateLimited",
                        "OpenRouter rate limit exceeded.")),
                    408 => Result<ModelResponse>.Failure(new Error("ProviderTimeout",
                        "Request timed out.")),
                    _ => Result<ModelResponse>.Failure(new Error("ProviderError",
                        $"OpenRouter returned HTTP {statusCode}."))
                };
            }

            try
            {
                var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
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
                            ["type"] = p.Type == ToolParameterType.String ? "string" : "integer",
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
