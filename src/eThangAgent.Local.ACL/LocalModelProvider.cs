using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using eThangAgent.ModelDomain;
using eThangAgent.Provider.Wire;
using eThangAgent.SharedKernel;

namespace eThangAgent.Local.ACL;

/// <summary>Sends domain chat requests to an OpenAI-compatible local server's chat
///     completions endpoint (llama.cpp, LM Studio, Ollama), streaming over Server-Sent
///     Events through the shared OpenAI-compatible stream core. The optional API key rides
///     an Authorization header only when configured — most local servers need none.
///     <see cref="ModelConfig.Effort"/> is deliberately never sent: local servers expose
///     no portable reasoning knob, and reasoning streams through the standard
///     <c>reasoning_content</c> field (see <see cref="LocalStreamVocabulary"/>).
///     <see cref="ModelConfig.Provider"/> (an OpenRouter upstream routing pin) is never
///     serialized either. Temperature passes through unvalidated — the server rejects
///     out-of-range values (HTTP 400 → ProviderError) rather than this ACL clamping
///     silently. The retry loop intentionally mirrors z.ai's provider shape
///     (SendOnceAsync / StatusOutcome / ReadJsonBodyAsync): at two copies, extracting a
///     shared abstraction is not worth the indirection.</summary>
public sealed class LocalModelProvider(HttpClient http, LocalConfiguration config,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    Func<double>? jitter = null) : IModelProvider
{
  private const string ProviderError = "ProviderError";

  private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
  private readonly LocalConfiguration _config = config ?? throw new ArgumentNullException(nameof(config));
  private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((span, token) => Task.Delay(span, token));
  private readonly Func<double> _jitter = jitter ?? Random.Shared.NextDouble;

  public async Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
  {
    int attempts = _config.Retry.MaxAttempts;
    for (int attempt = 1; attempt <= attempts; attempt++)
    {
      OpenAiCompatRequestCore.AttemptOutcome outcome = await SendOnceAsync(config, request, ct).ConfigureAwait(false);
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

  private async Task<OpenAiCompatRequestCore.AttemptOutcome> SendOnceAsync(ModelConfig config, ModelRequest request, CancellationToken ct)
  {
    try
    {
      using HttpRequestMessage httpRequest = CreateRequest(config, request, stream: false);
      using HttpResponseMessage response = await _http.SendAsync(httpRequest, ct).ConfigureAwait(false);
      return !response.IsSuccessStatusCode
        ? StatusOutcome((int)response.StatusCode, response.Headers.RetryAfter?.Delta, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))
        : OpenAiCompatRequestCore.AttemptOutcome.Final(await ReadJsonBodyAsync(response, ct).ConfigureAwait(false));
    }
    catch (OperationCanceledException)
    {
      return new OpenAiCompatRequestCore.AttemptOutcome(Result.Failure<ModelResponse>(new DomainError("ProviderTimeout",
          "Request timed out.")), Retryable: true, RetryAfter: null);
    }
    catch (HttpRequestException ex)
    {
      return new OpenAiCompatRequestCore.AttemptOutcome(Result.Failure<ModelResponse>(new DomainError(ProviderError, ex.Message)),
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

      OpenAiCompatRequestCore.AttemptOutcome outcome = await SendStreamingOnceAsync(config, request, contentSink, reasoningSink, ct).ConfigureAwait(false);
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

  private async Task<OpenAiCompatRequestCore.AttemptOutcome> SendStreamingOnceAsync(ModelConfig config, ModelRequest request,
      Action<string>? onContentDelta, Action<string>? onReasoningDelta, CancellationToken ct)
  {
    try
    {
      using HttpRequestMessage httpRequest = CreateRequest(config, request, stream: true);
      // Headers-read completion so the body surfaces incrementally instead of buffering.
      using HttpResponseMessage response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
      if (!response.IsSuccessStatusCode)
      {
        return StatusOutcome((int)response.StatusCode, response.Headers.RetryAfter?.Delta,
            await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
      }

      string? contentType = response.Content.Headers.ContentType?.MediaType;
      return contentType == "text/event-stream"
        ? OpenAiCompatRequestCore.AttemptOutcome.Final(await ReadSseStreamAsync(response, onContentDelta, onReasoningDelta, ct).ConfigureAwait(false))
        : OpenAiCompatRequestCore.AttemptOutcome.Final(await ReadJsonBodyAsync(response, ct).ConfigureAwait(false));
    }
    catch (OperationCanceledException)
    {
      return new OpenAiCompatRequestCore.AttemptOutcome(Result.Failure<ModelResponse>(new DomainError("ProviderTimeout",
          "Request timed out.")), Retryable: true, RetryAfter: null);
    }
    catch (HttpRequestException ex)
    {
      return new OpenAiCompatRequestCore.AttemptOutcome(Result.Failure<ModelResponse>(new DomainError(ProviderError, ex.Message)),
          Retryable: true, RetryAfter: null);
    }
    catch (IOException ex)
    {
      return new OpenAiCompatRequestCore.AttemptOutcome(Result.Failure<ModelResponse>(new DomainError(ProviderError,
          $"Connection lost while reading the provider stream: {ex.Message}")),
          Retryable: true, RetryAfter: null);
    }
  }

  /// <summary>Streams the response body through the shared OpenAI-compatible stream
  ///     core, supplying the local vocabulary (see <see cref="LocalStreamVocabulary.Instance"/>).</summary>
  private static Task<Result<ModelResponse>> ReadSseStreamAsync(HttpResponseMessage response,
      Action<string>? onContentDelta,
      Action<string>? onReasoningDelta,
      CancellationToken ct)
    => OpenAiCompatStreamCore.ReadSseStreamAsync(response, LocalStreamVocabulary.Instance,
        onContentDelta, onReasoningDelta, ct);

  private HttpRequestMessage CreateRequest(ModelConfig config, ModelRequest request, bool stream)
  {
    Dictionary<string, object?> bodyDict = new()
    {
      ["model"] = config.ModelId,
      ["messages"] = OpenAiCompatRequestCore.BuildMessages(request),
      ["max_tokens"] = config.MaxTokens,
      ["temperature"] = config.Temperature,
    };
    if (stream)
    {
      bodyDict["stream"] = true;
    }

    if (request.Tools is { Count: > 0 })
    {
      bodyDict["tools"] = request.Tools.Select(OpenAiCompatRequestCore.TranslateTool).ToArray();
    }

    HttpRequestMessage httpRequest = new(HttpMethod.Post, _config.ChatCompletionsEndpoint())
    {
      Content = JsonContent.Create(bodyDict)
    };
    if (_config.ApiKey is { } apiKey)
    {
      httpRequest.Headers.Authorization = new("Bearer", apiKey);
    }

    return httpRequest;
  }

  /// <summary>Maps an HTTP status to its error result plus retry classification: 408, 429,
  ///     and any 5xx are transient; everything else is permanent and fails immediately.
  ///     When the error body is not JSON, an excerpt of its text rides the message —
  ///     local servers answer failures with HTML error pages (the server is down, a proxy
  ///     intervened) and the excerpt is the only diagnostic the caller gets.</summary>
  private static OpenAiCompatRequestCore.AttemptOutcome StatusOutcome(int statusCode, TimeSpan? retryAfter, string errorBody)
  {
    string excerpt = Excerpt(errorBody);
    Result<ModelResponse> failure = statusCode switch
    {
      429 => Result.Failure<ModelResponse>(new DomainError("RateLimited",
          "Local server rate limit exceeded." + excerpt)),
      408 => Result.Failure<ModelResponse>(new DomainError("ProviderTimeout",
          "Request timed out.")),
      _ => Result.Failure<ModelResponse>(new DomainError(ProviderError,
          $"Local server returned HTTP {statusCode}." + excerpt))
    };
    return new OpenAiCompatRequestCore.AttemptOutcome(failure,
        Retryable: statusCode is 408 or 429 or >= 500,
        RetryAfter: retryAfter);
  }

  /// <summary>Extracts up to ~200 characters of a non-JSON error body for the error
  ///     message; JSON error bodies already parse into a structured message upstream.</summary>
  private static string Excerpt(string body)
  {
    if (string.IsNullOrWhiteSpace(body))
    {
      return string.Empty;
    }

    string trimmed = body.Trim();
    return trimmed.StartsWith('{') || trimmed.StartsWith('[')
        ? string.Empty
        : " " + trimmed[..Math.Min(trimmed.Length, 200)];
  }

  private static async Task<Result<ModelResponse>> ReadJsonBodyAsync(HttpResponseMessage response, CancellationToken ct)
  {
    try
    {
      JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
      return OpenAiCompatRequestCore.ParseChatCompletion(body);
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
}
