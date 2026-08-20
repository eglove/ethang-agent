using System.Net.Http.Json;
using System.Text.Json;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

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

    public async Task<Result<string>> SendAsync(ModelConfig config, string prompt, CancellationToken ct)
    {
        try
        {
            var requestBody = new
            {
                model = config.ModelId,
                messages = new[] { new { role = "user", content = prompt } },
                max_tokens = config.MaxTokens,
                temperature = config.Temperature
            };

            var requestUri = new Uri(_config.BaseUrl, "/api/v1/chat/completions");
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = JsonContent.Create(requestBody)
            };
            httpRequest.Headers.Add("Authorization", $"Bearer {_config.ApiKey}");

            using var response = await _http.SendAsync(httpRequest, ct);

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                return statusCode switch
                {
                    429 => Result<string>.Failure(new Error("RateLimited",
                        "OpenRouter rate limit exceeded.")),
                    408 => Result<string>.Failure(new Error("ProviderTimeout",
                        "Request timed out.")),
                    _ => Result<string>.Failure(new Error("ProviderError",
                        $"OpenRouter returned HTTP {statusCode}."))
                };
            }

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var content = body.GetProperty("choices")[0]
                .GetProperty("message").GetProperty("content").GetString();
            return Result<string>.Success(content ?? string.Empty);
        }
        catch (TaskCanceledException)
        {
            return Result<string>.Failure(new Error("ProviderTimeout",
                "Request timed out."));
        }
        catch (HttpRequestException ex)
        {
            return Result<string>.Failure(new Error("ProviderError", ex.Message));
        }
    }
}
