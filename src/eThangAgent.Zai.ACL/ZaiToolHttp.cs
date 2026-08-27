using System.Net.Http.Json;
using System.Text.Json;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Zai.ACL;

/// <summary>Shared plumbing for the z.ai capability-API tools: endpoint paths, JSON POST
///     with bearer auth, and the standard tool-error formatting.</summary>
internal static class ZaiToolHttp
{
  internal const string WebSearchPath = "/paas/v4/web_search";
  internal const string WebReaderPath = "/paas/v4/reader";
  internal const string TokenizerPath = "/paas/v4/tokenizer";
  internal const string ImagePath = "/paas/v4/images/generations";
  internal const string LayoutParsingPath = "/paas/v4/layout_parsing";
  internal const string TranscriptionsPath = "/paas/v4/audio/transcriptions";

  /// <summary>POSTs a JSON body with bearer auth and parses the response root. Non-success
  ///     statuses and malformed bodies become typed ProviderError results; a z.ai error
  ///     body ({code, message}) is surfaced verbatim when parseable.</summary>
  internal static async Task<Result<JsonElement>> PostJsonAsync(
      HttpClient http, ZaiConfiguration config, string path, object body, CancellationToken ct)
  {
    try
    {
      using HttpRequestMessage request = new(HttpMethod.Post, new Uri(config.BaseUrl, path))
      {
        Content = JsonContent.Create(body)
      };
      request.Headers.Add("Authorization", $"Bearer {config.ApiKey}");
      using HttpResponseMessage response = await http.SendAsync(request, ct).ConfigureAwait(false);
      if (!response.IsSuccessStatusCode)
      {
        string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        string? apiMessage = TryReadErrorMessage(text);
        return Result.Failure<JsonElement>(new DomainError("ProviderError",
            $"z.ai returned HTTP {(int)response.StatusCode}{(apiMessage is null ? "" : $": {apiMessage}")}"));
      }

      JsonElement root = await response.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
      return Result.Success(root);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
      throw; // budget/caller cancellation flows to ToolExecution, never masked
    }
    catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
    {
      return Result.Failure<JsonElement>(new DomainError("ProviderError", ex.Message));
    }
  }

  internal static ToolResult Err(DomainError error)
      => new($"Error [{error.Code}]: {error.Message}", true);

  private static string? TryReadErrorMessage(string body)
  {
    try
    {
      using JsonDocument doc = JsonDocument.Parse(body);
      return doc.RootElement.ValueKind == JsonValueKind.Object
          && doc.RootElement.TryGetProperty("message", out JsonElement message)
          && message.ValueKind == JsonValueKind.String
          ? message.GetString()
          : null;
    }
    catch (JsonException)
    {
      return null;
    }
  }
}
