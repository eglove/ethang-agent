using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using eThangAgent.ModelDomain;
using eThangAgent.Provider.Wire;
using eThangAgent.SharedKernel;

namespace eThangAgent.OpenRouter.ACL;

public class OpenRouterModelProvider(HttpClient http, OpenRouterConfiguration config,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    Func<double>? jitter = null) : IModelProvider
{
  private const string ProviderError = "ProviderError";
  private const int MaxErrorBodyCharacters = 512;
  private static readonly Routing EmptyRouting = new();

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
      Result<HttpRequestMessage> created = CreateRequest(config, request, stream: false);
      if (!created.IsSuccess)
      {
        return AttemptOutcome.Final(Result.Failure<ModelResponse>(created.Error));
      }

      using HttpRequestMessage httpRequest = created.Value;
      using HttpResponseMessage response = await _http.SendAsync(httpRequest, ct).ConfigureAwait(false);
      return !response.IsSuccessStatusCode
        ? await StatusOutcomeAsync(response, ct).ConfigureAwait(false)
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
      Result<HttpRequestMessage> created = CreateRequest(config, request, stream: true);
      if (!created.IsSuccess)
      {
        return AttemptOutcome.Final(Result.Failure<ModelResponse>(created.Error));
      }

      using HttpRequestMessage httpRequest = created.Value;
      // Headers-read completion so the body surfaces incrementally instead of buffering.
      using HttpResponseMessage response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
      if (!response.IsSuccessStatusCode)
      {
        return await StatusOutcomeAsync(response, ct).ConfigureAwait(false);
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

  private Result<HttpRequestMessage> CreateRequest(ModelConfig config, ModelRequest request, bool stream)
  {
    Dictionary<string, object?> bodyDict = new()
    {
      ["model"] = config.ModelId,
      ["input"] = ResponsesApiRequestCore.BuildInput(request),
      // max_output_tokens is the responses API's generation cap; chat-completions'
      // max_tokens is not a valid key on this surface.
      ["max_output_tokens"] = config.MaxTokens,
      ["temperature"] = config.Temperature,
    };
    if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
    {
      // The responses API's system channel: a single top-level instruction string
      // instead of a system-role message in the array.
      bodyDict["instructions"] = request.SystemPrompt;
    }

    ApplySamplingKnobs(bodyDict, config);
    if (stream)
    {
      // Usage rides the terminal response event on this surface; there is no
      // stream_options include_usage flag (that key is chat-completions-only).
      bodyDict["stream"] = true;
    }

    // The OR-side request settings (routing, server-side tools, plugins, budgets)
    // reach the wire here. Routing REPLACES the provider pinning (same "provider"
    // key); with no routing configured the pinning stays exactly as before.
    // Server-tool entries lead the tools array so user-defined tools follow;
    // enabled plugins ride the "plugins" array. Default sections contribute zero
    // keys, so a default ProviderSettings leaves the body unchanged.

    if (!string.IsNullOrWhiteSpace(config.Provider))
    {
      bodyDict["provider"] = new { only = new[] { config.Provider } };
    }

    Result<OpenRouterRequestSettings?> settingsResult = ParseSettings(config.ProviderSettings);
    if (!settingsResult.IsSuccess)
    {
      return Result.Failure<HttpRequestMessage>(settingsResult.Error);
    }

    OpenRouterRequestSettings? settings = settingsResult.ValueOrNull;
    if (settings?.Routing is { } routing && routing != EmptyRouting)
    {
      bodyDict["provider"] = RoutingBody(routing);
    }

    if (settings?.ServerTools.MaxToolCalls is { } maxToolCalls)
    {
      bodyDict["max_tool_calls"] = maxToolCalls;
    }

    if (settings?.ServerTools.StopServerToolsWhen is { } stopWhen)
    {
      if (ValidateStopConditions(stopWhen) is { } fault)
      {
        return Result.Failure<HttpRequestMessage>(new DomainError("InvalidProviderSettings",
            $"Malformed OpenRouter provider settings: {fault}"));
      }

      bodyDict["stop_server_tools_when"] = stopWhen;
    }

    // Only sent when the user picked a level (the effort picker); OpenRouter's own
    // default applies otherwise, and it normalizes the level to what the model supports.
    if (config.Effort is { } effort)
    {
      bodyDict["reasoning"] = new { effort = OpenRouterReasoningEffort.ToWire(effort) };
    }

    // The server-tool entries are computed ONCE; enablement (the tools key existing
    // at all) derives from the entries themselves, so adding a twelfth tool can
    // never desync the enablement check from the entries actually emitted.
    List<object> serverToolEntries = [];
    if (settings is not null)
    {
      serverToolEntries.AddRange(ServerToolEntries(settings.ServerTools));
    }

    if (serverToolEntries.Count > 0 || request.Tools is { Count: > 0 })
    {
      List<object> tools = serverToolEntries;
      if (request.Tools is { Count: > 0 })
      {
        tools.AddRange(request.Tools.Select(ResponsesApiRequestCore.TranslateTool));
      }

      bodyDict["tools"] = tools.ToArray();
    }

    if (settings?.Plugins is { } plugins && (plugins.WebGrounding || plugins.ResponseHealing))
    {
      List<object> entries = [];
      if (plugins.WebGrounding)
      {
        entries.Add(plugins.WebGroundingEngine is { } engine
            ? new Dictionary<string, object?> { ["id"] = "web", ["engine"] = engine }
            : new Dictionary<string, object?> { ["id"] = "web" });
      }

      if (plugins.ResponseHealing)
      {
        entries.Add(new Dictionary<string, object?> { ["id"] = "response-healing" });
      }

      bodyDict["plugins"] = entries.ToArray();
    }

    // Ownership of the request transfers through the Result to the send path, whose
    // using disposes it; CA2000 cannot see the transfer, hence the scoped deviation.
#pragma warning disable CA2000 // Ownership transfers to the caller, which disposes it.
    HttpRequestMessage httpRequest = new(HttpMethod.Post, _config.Endpoint("/api/v1/responses"))
    {
      Content = JsonContent.Create(bodyDict)
    };
#pragma warning restore CA2000
    httpRequest.Headers.Add("Authorization", $"Bearer {_config.ApiKey}");
    return Result.Success(httpRequest);
  }

  /// <summary>Strict stop-condition validation at the send boundary: every condition
  ///     must be one of the five known types carrying its own field. Returns the fault
  ///     message for the first malformed condition, or null when all are valid — a
  ///     malformed condition is a named InvalidProviderSettings failure, never a
  ///     silently dropped filter, never a 400 from the provider.</summary>
  private static string? ValidateStopConditions(IReadOnlyList<ServerToolStopCondition> conditions)
  {
    foreach (ServerToolStopCondition condition in conditions)
    {
      if (condition.DescribeFault() is { } fault)
      {
        return fault;
      }
    }

    return null;
  }

  /// <summary>Emits each set sampling knob under its OpenRouter wire key; null knobs
  ///     never appear on the wire. Verbosity maps to the wire string
  ///     (low | medium | high | xhigh | max).</summary>
  private static void ApplySamplingKnobs(Dictionary<string, object?> body, ModelConfig config)
  {
    if (config.TopP is { } topP)
    {
      body["top_p"] = topP;
    }

    if (config.TopK is { } topK)
    {
      body["top_k"] = topK;
    }

    if (config.FrequencyPenalty is { } frequencyPenalty)
    {
      body["frequency_penalty"] = frequencyPenalty;
    }

    if (config.PresencePenalty is { } presencePenalty)
    {
      body["presence_penalty"] = presencePenalty;
    }

    if (config.RepetitionPenalty is { } repetitionPenalty)
    {
      body["repetition_penalty"] = repetitionPenalty;
    }

    if (config.MinP is { } minP)
    {
      body["min_p"] = minP;
    }

    if (config.TopA is { } topA)
    {
      body["top_a"] = topA;
    }

    if (config.Seed is { } seed)
    {
      body["seed"] = seed;
    }

    if (config.Verbosity is { } verbosity)
    {
      body["verbosity"] = verbosity switch
      {
        VerbosityLevel.Low => "low",
        VerbosityLevel.Medium => "medium",
        VerbosityLevel.High => "high",
        VerbosityLevel.XHigh => "xhigh",
        VerbosityLevel.Max => "max",
        _ => throw new InvalidOperationException("Unknown verbosity level: " + verbosity),
      };
    }

    if (config.ParallelToolCalls is { } parallelToolCalls)
    {
      body["parallel_tool_calls"] = parallelToolCalls;
    }
  }

  /// <summary>Parses the persisted provider settings: null/unset settings parse to
  ///     null and leave the body untouched; malformed JSON flows through the Result
  ///     error contract as a named InvalidProviderSettings failure — an expected
  ///     environmental failure is data, never an exception the send paths' catch
  ///     sets (cancellation, transport) would miss.</summary>
  private static Result<OpenRouterRequestSettings?> ParseSettings(string? providerSettings)
  {
    try
    {
      return Result.Success(OpenRouterRequestSettings.Parse(providerSettings));
    }
    catch (JsonException ex)
    {
      return Result.Failure<OpenRouterRequestSettings?>(new DomainError("InvalidProviderSettings",
          $"Malformed OpenRouter provider settings: {ex.Message}"));
    }
  }

  /// <summary>The full routing object for body["provider"]: every non-null member under
  ///     its snake_case wire key, matching the T3 serializer's routing section shape;
  ///     null members are emitted only when present.</summary>
  private static Dictionary<string, object?> RoutingBody(Routing routing)
  {
    Dictionary<string, object?> body = [];
    if (routing.Order is { } order)
    {
      body["order"] = order;
    }

    if (routing.Only is { } only)
    {
      body["only"] = only;
    }

    if (routing.Ignore is { } ignore)
    {
      body["ignore"] = ignore;
    }

    if (routing.AllowFallbacks is { } allowFallbacks)
    {
      body["allow_fallbacks"] = allowFallbacks;
    }

    if (routing.Sort is { } sort)
    {
      body["sort"] = sort;
    }

    if (routing.Quantizations is { } quantizations)
    {
      body["quantizations"] = quantizations;
    }

    if (routing.RequireParameters is { } requireParameters)
    {
      body["require_parameters"] = requireParameters;
    }

    if (routing.DataCollection is { } dataCollection)
    {
      body["data_collection"] = dataCollection;
    }

    if (routing.Models is { } models)
    {
      body["models"] = models;
    }

    if (routing.Route is { } route)
    {
      body["route"] = route;
    }

    return body;
  }

  /// <summary>The wire entries for the enabled server tools, in declared order: each
  ///     entry is an object with a type member carrying the tool's wire type string;
  ///     v1 carries no per-tool parameters. Type strings follow the task-4 wire
  ///     ruling: SearchModels is openrouter:experimental__search_models.</summary>
  private static object[] ServerToolEntries(ServerTools tools)
  {
    List<object> entries = [];
    void AddIf(bool enabled, string wireType)
    {
      if (enabled)
      {
        entries.Add(new Dictionary<string, object?> { ["type"] = wireType });
      }
    }

    AddIf(tools.WebSearch, "openrouter:web_search");
    AddIf(tools.WebFetch, "openrouter:web_fetch");
    AddIf(tools.Datetime, "openrouter:datetime");
    AddIf(tools.ImageGeneration, "openrouter:image_generation");
    AddIf(tools.Shell, "openrouter:shell");
    AddIf(tools.ApplyPatch, "openrouter:apply_patch");
    AddIf(tools.Fusion, "openrouter:fusion");
    AddIf(tools.Advisor, "openrouter:advisor");
    AddIf(tools.Subagent, "openrouter:subagent");
    AddIf(tools.SearchModels, "openrouter:experimental__search_models");
    AddIf(tools.ToolSearch, "openrouter:tool_search");
    return [.. entries];
  }

  /// <summary>Maps an HTTP status to its error result plus retry classification: 408,
  ///     429, and any 5xx are transient; everything else is permanent and fails
  ///     immediately. The response body — OpenRouter's JSON error naming the actual
  ///     fault — is read (capped) and included in the message: an error is
  ///     information for whoever can act on it, never a bare status code.</summary>
  private static async Task<AttemptOutcome> StatusOutcomeAsync(HttpResponseMessage response, CancellationToken ct)
  {
    string? detail = null;
    try
    {
      string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
      if (body.Length > 0)
      {
        detail = body.Length <= MaxErrorBodyCharacters ? body : body[..MaxErrorBodyCharacters] + "…";
      }
    }
    catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException
        or NotSupportedException or ObjectDisposedException)
    {
      // Best effort: a body that cannot be read (disposed stream, cancelled read)
      // leaves the message at the bare status — never replaces the failure.
    }

    Result<ModelResponse> failure = (int)response.StatusCode switch
    {
      429 => Result.Failure<ModelResponse>(new DomainError("RateLimited",
          "OpenRouter rate limit exceeded.")),
      408 => Result.Failure<ModelResponse>(new DomainError("ProviderTimeout",
          "Request timed out.")),
      _ => Result.Failure<ModelResponse>(new DomainError(ProviderError,
          detail is null
              ? $"OpenRouter returned HTTP {(int)response.StatusCode}."
              : $"OpenRouter returned HTTP {(int)response.StatusCode}: {detail}")),
    };
    return new AttemptOutcome(failure,
        Retryable: response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            or >= HttpStatusCode.InternalServerError,
        RetryAfter: response.Headers.RetryAfter?.Delta);
  }

  private static async Task<Result<ModelResponse>> ReadJsonBodyAsync(HttpResponseMessage response, CancellationToken ct)
  {
    try
    {
      JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
      return ResponsesApiRequestCore.ParseResponse(body);
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

  /// <summary>Streams the response body through the shared Responses-API stream core.
  ///     The event names travel inside each data frame's "type" field (OpenRouter's
  ///     live framing); the core tolerates both that and the canonical event-line
  ///     framing.</summary>
  private static Task<Result<ModelResponse>> ReadSseStreamAsync(HttpResponseMessage response,
      Action<string>? onContentDelta,
      Action<string>? onReasoningDelta,
      CancellationToken ct)
    => ResponsesApiStreamCore.ReadSseStreamAsync(response, onContentDelta, onReasoningDelta, ct);

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
}
