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
    Func<double>? jitter = null,
    IModelCatalog? catalog = null) : IModelProvider
{
  private const string ProviderError = "ProviderError";
  private const string ContextWindowExceeded = "ContextWindowExceeded";

  /// <summary>Substrings that identify a context-window overflow in an HTTP 400 body.
  ///     OpenRouter forwards the upstream provider's error JSON verbatim, so the markers
  ///     span the providers behind it: OpenAI ("maximum context length",
  ///     "context_length_exceeded", "reduce the length of the messages"), Anthropic
  ///     ("prompt is too long"), Gemini ("exceeds the maximum token count"), and the
  ///     canonical overflow phrasing other gateways emit ("context length exceeded",
  ///     "input length and max_tokens exceed context limit"). Matched
  ///     case-insensitively: providers capitalize inconsistently.</summary>
  private static readonly string[] ContextOverflowMarkers =
  [
      "maximum context length",
      "context_length_exceeded",
      "context length exceeded",
      "reduce the length of the messages",
      "prompt is too long",
      "exceeds the maximum token count",
      "input length and max_tokens exceed context limit",
  ];
  private const int MaxErrorBodyCharacters = 512;

  /// <summary>OpenRouter's session_id length limit (256 characters), enforced at the
  ///     request boundary: a longer id is a named failure, never a truncated key.</summary>
  private const int MaxSessionIdCharacters = 256;

  private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
  private readonly OpenRouterConfiguration _config = config ?? throw new ArgumentNullException(nameof(config));
  private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((span, token) => Task.Delay(span, token));
  private readonly Func<double> _jitter = jitter ?? Random.Shared.NextDouble;

  /// <summary>The catalog the Cache Floor order is computed from, or null when the caller wired
  ///     none. OPTIONAL ON PURPOSE: the variant then degrades to the provider's own routing
  ///     instead of failing a turn over a missing seam.</summary>
  private readonly IModelCatalog? _catalog = catalog;

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
      Result<Dictionary<string, object?>> built = await BuildBodyOrFailAsync(config, request, stream: false, ct).ConfigureAwait(false);
      if (!built.IsSuccess)
      {
        return AttemptOutcome.Final(Result.Failure<ModelResponse>(built.Error));
      }

      using HttpRequestMessage httpRequest = NewRequest(built.Value);
      // Timing wraps the provider call itself (issue #292): dispatch to settled, not the
      // iteration, which would mostly measure our own tool dispatch and local work.
      long started = Stopwatch.GetTimestamp();
      using HttpResponseMessage response = await _http.SendAsync(httpRequest, ct).ConfigureAwait(false);
      if (!response.IsSuccessStatusCode)
      {
        return await StatusOutcomeAsync(response, ct).ConfigureAwait(false);
      }

      Result<ModelResponse> parsed = await ReadJsonBodyAsync(response, ct).ConfigureAwait(false);
      return AttemptOutcome.Final(WithDuration(parsed, Stopwatch.GetElapsedTime(started)));
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
      Result<Dictionary<string, object?>> built = await BuildBodyOrFailAsync(config, request, stream: true, ct).ConfigureAwait(false);
      if (!built.IsSuccess)
      {
        return AttemptOutcome.Final(Result.Failure<ModelResponse>(built.Error));
      }

      using HttpRequestMessage httpRequest = NewRequest(built.Value);
      // Same measurement as the non-streaming path (issue #292): the clock starts at
      // dispatch and stops when the response is settled, so the two paths report a
      // comparable number. The stream core contributes FirstTokenMs; this contributes the
      // total, and neither can be produced by the other.
      long started = Stopwatch.GetTimestamp();
      // Headers-read completion so the body surfaces incrementally instead of buffering.
      using HttpResponseMessage response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
      if (!response.IsSuccessStatusCode)
      {
        return await StatusOutcomeAsync(response, ct).ConfigureAwait(false);
      }

      string? contentType = response.Content.Headers.ContentType?.MediaType;
      Result<ModelResponse> parsed = contentType == "text/event-stream"
        ? await ReadSseStreamAsync(response, onContentDelta, onReasoningDelta, ct).ConfigureAwait(false)
        : await ReadJsonBodyAsync(response, ct).ConfigureAwait(false);
      return AttemptOutcome.Final(WithDuration(parsed, Stopwatch.GetElapsedTime(started)));
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

  /// <summary>Builds the request body and returns the failure when it cannot be built; the
  ///     successful body is handed back so the CALLER constructs the
  ///     <see cref="HttpRequestMessage"/> and owns its disposal.</summary>
  private Task<Result<Dictionary<string, object?>>> BuildBodyOrFailAsync(ModelConfig config,
      ModelRequest request, bool stream, CancellationToken ct)
      => BuildRequestBodyAsync(config, request, stream, ct);

  /// <summary>Creates the outbound request for a built body. The caller owns the
  ///     returned instance and disposes it (its using statement).</summary>
  private HttpRequestMessage NewRequest(Dictionary<string, object?> body)
  {
    HttpRequestMessage httpRequest = new(HttpMethod.Post, _config.Endpoint("/api/v1/responses"))
    {
      Content = JsonContent.Create(body)
    };
    httpRequest.Headers.Add("Authorization", $"Bearer {_config.ApiKey}");
    return httpRequest;
  }

  /// <summary>Builds the responses-API body dictionary: the base fields, the system prompt and
  ///     session id when present, the sampling knobs, the OR-side provider settings, then the
  ///     Cache Floor order when that variant was chosen. Every named failure (session id too
  ///     long, malformed settings, malformed stop conditions) surfaces through the Result
  ///     contract unchanged from the pre-extraction behavior.</summary>
  private async Task<Result<Dictionary<string, object?>>> BuildRequestBodyAsync(ModelConfig config,
      ModelRequest request, bool stream, CancellationToken ct)
  {
    Dictionary<string, object?> bodyDict = BaseBody(config, request);
    DomainError? fault = AttachSessionId(bodyDict, request);
    if (fault is null)
    {
      ApplySamplingKnobs(bodyDict, config);
      if (stream)
      {
        // Usage rides the terminal response event on this surface; there is no
        // stream_options include_usage flag (that key is chat-completions-only).
        bodyDict["stream"] = true;
      }

      fault = ParseAndApplySettings(bodyDict, config, request);
    }

    fault ??= await ApplyCacheFloorOrderAsync(bodyDict, config, ct).ConfigureAwait(false);

    return fault is { } failure
        ? Result.Failure<Dictionary<string, object?>>(failure)
        : Result.Success(bodyDict);
  }

  /// <summary>Writes the Cache Floor order onto the body's <c>provider</c> object: the model's
  ///     endpoints sorted by advertised cache-read price, cheapest first, with fallbacks
  ///     allowed so an unavailable endpoint falls through instead of failing the turn.
  ///     <para>WHY <c>order</c> AND NOT <c>only</c>: measured 2026-10-28, a stale slug in
  ///     <c>only</c> fails the entire request ("No allowed providers are available for the
  ///     selected model"), while <c>order</c> tolerates it and falls back. A routing variant
  ///     must never make a model unservable.</para>
  ///     <para>WHY NO <c>sort</c> KEY: the provider's sort enum is closed to
  ///     price/throughput/latency/exacto and rejects anything else with HTTP 400, so an
  ///     explicit order list is the only expressible form.</para>
  ///     <para>DEGRADES TO NO PROVIDER BLOCK when the catalog is unwired, the read fails, or no
  ///     endpoint advertises a cache-read price: an empty order would be a routing instruction
  ///     that names nothing.</para></summary>
  private async Task<DomainError?> ApplyCacheFloorOrderAsync(Dictionary<string, object?> bodyDict,
      ModelConfig config, CancellationToken ct)
  {
    if (config.Variant != ModelVariant.CacheFloor || _catalog is null)
    {
      return null;
    }

    Result<IReadOnlyList<ModelProviderEntry>> entries = await _catalog.GetAsync(ct).ConfigureAwait(false);
    if (!entries.IsSuccess)
    {
      return null;
    }

    string baseModel = ModelVariants.BaseOf(config.ModelId);
    IReadOnlyList<ModelProviderEntry> rows = [.. entries.Value.Where(e => e.ModelId == baseModel)];

    // NO ADVERTISED CACHE-READ PRICE MEANS NO ORDER. Slugs deliberately RETAINS a row whose
    // cache-read price is unknown and sorts it last (an unknown price is a fact about the
    // endpoint, not a reason to hide it), so a non-empty list does NOT prove a price was
    // advertised. An order naming only unpriced endpoints would be a routing instruction the
    // catalog never justified, so the variant degrades to the provider's own routing instead.
    if (!rows.Any(e => e.Variant.Slug is { Length: > 0 } && e.PromptCacheReadPricePerToken is >= 0m))
    {
      return null;
    }

    IReadOnlyList<string> slugs = CacheFloorOrder.Slugs(rows);
    if (slugs.Count == 0)
    {
      return null;
    }

    bodyDict["provider"] = new Dictionary<string, object?>
    {
      ["order"] = slugs,
      ["allow_fallbacks"] = true,
    };
    return null;
  }

  /// <summary>Parses the provider settings and applies them to the body; a parse fault or a
  ///     malformed stop condition surfaces as the returned DomainError — never silently
  ///     dropped.
  ///     <para>Provider settings do not write a routing block. Standard, Nitro and Floor use
  ///     the model id; Cache Floor's preference order is attached separately after settings
  ///     validation, without the retired restrictive <c>only</c> pin.</para></summary>
  private static DomainError? ParseAndApplySettings(Dictionary<string, object?> bodyDict,
      ModelConfig config, ModelRequest request)
  {
    Result<OpenRouterRequestSettings?> settingsResult = ParseSettings(config.ProviderSettings);
    return settingsResult.IsSuccess
        ? ApplyProviderSettings(bodyDict, settingsResult.ValueOrNull, request, config)
        : settingsResult.Error;
  }

  /// <summary>The base body fields; the system prompt rides the responses API's
  ///     system channel — a single top-level instruction string instead of a
  ///     system-role message in the array.</summary>
  private static Dictionary<string, object?> BaseBody(ModelConfig config, ModelRequest request)
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
      bodyDict["instructions"] = request.SystemPrompt;
    }

    return bodyDict;
  }

  /// <summary>Sets body["session_id"] for OpenRouter sticky sessions (prompt caching):
  ///     the session id rides the body's top-level field and OpenRouter uses it
  ///     directly as the sticky routing key instead of deriving one from message
  ///     hashing — the right key for multi-turn agentic requests whose opening
  ///     messages change every turn.
  ///     https://openrouter.ai/docs/guides/best-practices/prompt-caching
  ///     Returns the typed failure when the id exceeds the API's limit — a longer
  ///     id is a named failure, never a truncated key.</summary>
  private static DomainError? AttachSessionId(Dictionary<string, object?> bodyDict, ModelRequest request)
  {
    if (request.SessionId is not { } sessionId)
    {
      return null;
    }

    if (sessionId.ToString().Length > MaxSessionIdCharacters)
    {
      return new DomainError("InvalidRequest",
          $"SessionId exceeds the API's {MaxSessionIdCharacters}-character limit.");
    }

    bodyDict["session_id"] = sessionId.ToString();
    return null;
  }

  /// <summary>The OR-side request settings (server-side tools, plugins, budgets)
  ///     reach the wire here. Server-tool entries lead the tools array so user-defined
  ///     tools follow; enabled plugins ride the "plugins" array. Default sections
  ///     contribute zero keys, so a default ProviderSettings leaves the body unchanged.
  ///     Returns the fault when the stop conditions are malformed.
  ///     <para>THE PROVIDER PIN IS THE ONLY THING THAT WRITES body["provider"] (issue #297,
  ///     D2): routing is deleted, so nothing competes with the pin for that key. The user
  ///     chooses one model/provider pair in Model Settings and that pair either serves or
  ///     the call fails - there is no routing object left to silently replace it.</para></summary>
  private static DomainError? ApplyProviderSettings(Dictionary<string, object?> bodyDict,
      OpenRouterRequestSettings? settings, ModelRequest request, ModelConfig config)
  {
    if (settings?.ServerTools.MaxToolCalls is { } maxToolCalls)
    {
      bodyDict["max_tool_calls"] = maxToolCalls;
    }

    if (ApplyStopConditions(bodyDict, settings) is { } stopFault)
    {
      return stopFault;
    }

    if (ReasoningBody(config.Effort, settings?.ReasoningSummary) is { } reasoning)
    {
      bodyDict["reasoning"] = reasoning;
    }

    ApplyServerTools(bodyDict, settings, request);
    ApplyPlugins(bodyDict, settings);
    return null;
  }

  /// <summary>Builds the request's "reasoning" object from the two independent knobs:
  ///     effort (the picker's level) and summary (the display channel's level). Each
  ///     member is emitted only when set, so effort-only, summary-only, and both
  ///     together are all well-formed; neither set yields null, and the key is then
  ///     omitted entirely — OpenRouter's own defaults apply. The summary member is
  ///     the provider's display separation: it changes what streams to the user and
  ///     nothing else (the request's "input" array is untouched by it).</summary>
  private static Dictionary<string, object?>? ReasoningBody(
      ReasoningEffort? effort, ReasoningSummaryLevel? summary)
  {
    if (effort is null && summary is null)
    {
      return null;
    }

    Dictionary<string, object?> reasoning = [];
    if (effort is { } level)
    {
      reasoning["effort"] = OpenRouterReasoningEffort.ToWire(level);
    }

    if (summary is { } summaryLevel)
    {
      reasoning["summary"] = OpenRouterReasoningSummary.ToWire(summaryLevel);
    }

    return reasoning;
  }

  /// <summary>Sets the stop_server_tools_when budget when configured; every condition
  ///     is validated strictly at the send boundary — a malformed condition is a
  ///     named InvalidProviderSettings failure, never a silently dropped filter,
  ///     never a 400 from the provider.</summary>
  private static DomainError? ApplyStopConditions(Dictionary<string, object?> bodyDict,
      OpenRouterRequestSettings? settings)
  {
    if (settings?.ServerTools.StopServerToolsWhen is not { } stopWhen)
    {
      return null;
    }

    if (ValidateStopConditions(stopWhen) is { } fault)
    {
      return new DomainError("InvalidProviderSettings",
          $"Malformed OpenRouter provider settings: {fault}");
    }

    bodyDict["stop_server_tools_when"] = stopWhen;
    return null;
  }

  /// <summary>Sets the "tools" array when any tool is present: the server-tool
  ///     entries lead so user-defined tools follow. The server-tool entries are
  ///     computed ONCE; enablement (the tools key existing at all) derives from the
  ///     entries themselves, so adding a twelfth tool can never desync the
  ///     enablement check from the entries actually emitted.</summary>
  private static void ApplyServerTools(Dictionary<string, object?> bodyDict,
      OpenRouterRequestSettings? settings, ModelRequest request)
  {
    List<object> tools = settings is null ? [] : [.. ServerToolEntries(settings.ServerTools)];
    if (request.Tools is { Count: > 0 })
    {
      tools.AddRange(request.Tools.Select(ResponsesApiRequestCore.TranslateTool));
    }

    if (tools.Count > 0)
    {
      bodyDict["tools"] = tools.ToArray();
    }
  }

  /// <summary>Sets the "plugins" array when any plugin is enabled; each enabled
  ///     plugin rides its entry keyed by the plugin id.</summary>
  private static void ApplyPlugins(Dictionary<string, object?> bodyDict, OpenRouterRequestSettings? settings)
  {
    if (settings?.Plugins is { } plugins && (plugins.WebGrounding || plugins.ResponseHealing))
    {
      bodyDict["plugins"] = PluginEntries(plugins).ToArray();
    }
  }

  private static List<object> PluginEntries(Plugins plugins)
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

    return entries;
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
    ApplyDistributionKnobs(body, config);
    ApplyTuningKnobs(body, config);
    if (config.Verbosity is { } verbosity)
    {
      body["verbosity"] = VerbosityWire(verbosity);
    }
  }

  /// <summary>Emits the distribution-shaped sampling knobs (top_p, top_k, min_p,
  ///     top_a) under their OpenRouter wire keys; null knobs never appear on the
  ///     wire.</summary>
  private static void ApplyDistributionKnobs(Dictionary<string, object?> body, ModelConfig config)
  {
    if (config.TopP is { } topP)
    {
      body["top_p"] = topP;
    }

    if (config.TopK is { } topK)
    {
      body["top_k"] = topK;
    }

    if (config.MinP is { } minP)
    {
      body["min_p"] = minP;
    }

    if (config.TopA is { } topA)
    {
      body["top_a"] = topA;
    }
  }

  /// <summary>Emits the tuning-shaped sampling knobs (frequency/presence/repetition
  ///     penalties, seed, parallel tool calls) under their OpenRouter wire keys;
  ///     null knobs never appear on the wire.</summary>
  private static void ApplyTuningKnobs(Dictionary<string, object?> body, ModelConfig config)
  {
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

    if (config.Seed is { } seed)
    {
      body["seed"] = seed;
    }

    if (config.ParallelToolCalls is { } parallelToolCalls)
    {
      body["parallel_tool_calls"] = parallelToolCalls;
    }
  }

  /// <summary>Maps a verbosity level to its wire string (low | medium | high |
  ///     xhigh | max); an unknown level throws — the validated enum is never
  ///     silently coerced.</summary>
  private static string VerbosityWire(VerbosityLevel verbosity)
      => verbosity switch
      {
        VerbosityLevel.Low => "low",
        VerbosityLevel.Medium => "medium",
        VerbosityLevel.High => "high",
        VerbosityLevel.XHigh => "xhigh",
        VerbosityLevel.Max => "max",
        _ => throw new InvalidOperationException("Unknown verbosity level: " + verbosity),
      };
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
    string? detail = await ErrorDetailAsync(response, ct).ConfigureAwait(false);
    return new AttemptOutcome(StatusFailure(response.StatusCode, detail),
        Retryable: response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            or >= HttpStatusCode.InternalServerError,
        RetryAfter: response.Headers.RetryAfter?.Delta);
  }

  /// <summary>Caps an error body at the wire limit; the ellipsis rides only a
  ///     body the cap actually trimmed.</summary>
  private static string CappedLength(string body)
      => body.Length <= MaxErrorBodyCharacters ? body : body[..MaxErrorBodyCharacters] + "…";

  /// <summary>Reads (capped) the error body OpenRouter serves with a failed status:
  ///     the provider's JSON error naming the actual fault — information for
  ///     whoever can act on it. A body that cannot be read (disposed stream,
  ///     cancelled read) returns null — best effort, it never replaces the failure
  ///     with a read fault and leaves the message at the bare status.</summary>
  private static async Task<string?> ErrorDetailAsync(HttpResponseMessage response, CancellationToken ct)
  {
    try
    {
      string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
      return body.Length switch
      {
        0 => null,
        _ => CappedLength(body),
      };
    }
    catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException
        or NotSupportedException or ObjectDisposedException)
    {
      return null;
    }
  }

  /// <summary>Maps an HTTP status to its error result: 429 is a rate limit, 408 a
  ///     timeout, and a 400 whose body names the context-length fault is its own
  ///     failure — the agent loop, not the wire layer, decides what an overflow
  ///     means (one forced compaction, one retry). Everything else is a
  ///     ProviderError. Retry classification stays with the caller.</summary>
  private static Result<ModelResponse> StatusFailure(HttpStatusCode statusCode, string? detail)
      => (int)statusCode switch
      {
        429 => Result.Failure<ModelResponse>(new DomainError("RateLimited",
            "OpenRouter rate limit exceeded.")),
        408 => Result.Failure<ModelResponse>(new DomainError("ProviderTimeout",
            "Request timed out.")),
        // Retryable stays false here: the identical request would fail identically.
        400 when IsContextOverflow(detail) => Result.Failure<ModelResponse>(new DomainError(ContextWindowExceeded,
            StatusDetail((int)statusCode, detail))),
        _ => Result.Failure<ModelResponse>(new DomainError(ProviderError,
            StatusDetail((int)statusCode, detail))),
      };

  /// <summary>The failed request's message: the bare status when no body could be
  ///     read, the (capped) body otherwise.</summary>
  private static string StatusDetail(int statusCode, string? detail)
      => detail is null
          ? $"OpenRouter returned HTTP {statusCode}."
          : $"OpenRouter returned HTTP {statusCode}: {detail}";

  /// <summary>True when the (capped) error body names the context-length fault. A body
  ///     that cannot be read is not an overflow: classification stays conservative.</summary>
  private static bool IsContextOverflow(string? detail)
      => detail is not null && ContextOverflowMarkers.Any(marker =>
          detail.Contains(marker, StringComparison.OrdinalIgnoreCase));

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

  /// <summary>Stamps a successful response with how long the call took (issue #292). A
  ///     FAILURE is returned untouched: a failed call produced no response to describe, and
  ///     a duration on an error result would be a number nobody can use.
  ///     <para>Whole milliseconds, truncated. A call that finished inside one millisecond
  ///     reports 0 — a real measurement, not the absence of one; only a response no caller
  ///     timed carries null.</para></summary>
  private static Result<ModelResponse> WithDuration(Result<ModelResponse> result, TimeSpan elapsed)
      => result.IsSuccess
          ? Result.Success(result.Value with { DurationMs = (long)elapsed.TotalMilliseconds })
          : result;

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
