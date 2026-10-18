using System.Globalization;
using System.Text.Json;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2213 // _http is injected (DI-owned); only _gate is owned and disposed
namespace eThangAgent.OpenRouter.ACL;

/// <summary>Implements <see cref="IModelCatalog"/> by fetching OpenRouter's /api/v1/models
/// endpoint (Phase 1) and /api/v1/models/{id}/endpoints (Phase 2) to build a fully expanded
/// list of model+provider pairs. Caches the result in-memory with a configurable TTL.
/// Thread-safe via SemaphoreSlim to prevent stampede on cache expiry.</summary>
public sealed class OpenRouterCatalogClient(
    HttpClient http,
    OpenRouterConfiguration config,
    IModelCatalogCache? cache = null,
    string? provider = null,
    Action<Exception>? onRefreshFault = null) : IModelCatalog, IDisposable
{
  private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
  private readonly OpenRouterConfiguration _config = config ?? throw new ArgumentNullException(nameof(config));
  private readonly IModelCatalogCache? _cache = cache;
  // A provider id alone cannot distinguish the real API from a configured proxy or
  // test server. Ignore legacy provider-only rows: their source cannot be verified.
  private readonly string? _cacheKey = string.IsNullOrEmpty(provider)
      ? null
      : $"{provider}|{config.Endpoint("/api/v1/models").AbsoluteUri}";
  private readonly Action<Exception>? _onRefreshFault = onRefreshFault;
  private readonly SemaphoreSlim _gate = new(1, 1);
  private readonly CancellationTokenSource _lifetime = new();
  private bool _disposed;
  private readonly Lock _refreshLock = new();
  private IReadOnlyList<ModelProviderEntry>? _cached;
  private DateTimeOffset _fetchedAt;
  private Task<Result<IReadOnlyList<ModelProviderEntry>>>? _refresh;

  /// <summary>The in-flight background revalidation, or null when none is running.
  ///     Exposed for deterministic tests; production never awaits it.</summary>
  internal Task? PendingRefresh
  {
    get
    {
      lock (_refreshLock)
      {
        return _refresh;
      }
    }
  }

  public async Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default)
  {
    // Stale-while-revalidate: a warm - even stale - memory copy is served at once and
    // refreshed in the background; only a cold cache blocks on the network.
    if (_cached is not null)
    {
      if (IsFresh())
      {
        return Result.Success(_cached);
      }

      StartBackgroundRefresh();
      return Result.Success(_cached);
    }

    await _gate.WaitAsync(ct).ConfigureAwait(false);
    try
    {
      // Named decision (CA1508): the double-check is load-bearing - another thread may
      // have completed a fetch while this call waited on the gate, so the field is
      // re-read here even though the pre-gate check already saw it null.
#pragma warning disable CA1508 // Avoid dead conditional code
      if (_cached is not null)
      {
        if (IsFresh())
        {
          return Result.Success(_cached);
        }

        StartBackgroundRefresh();
        return Result.Success(_cached);
      }
#pragma warning restore CA1508 // Avoid dead conditional code

      // Cold: hydrate from the persisted snapshot once, then fall through to a network
      // fetch when nothing is stored (or the stored copy is unusable).
      await HydrateFromDiskAsync(ct).ConfigureAwait(false);
      if (_cached is not null)
      {
        // Disk freshness is not evidence that this process has revalidated the source.
        StartBackgroundRefresh();
        return Result.Success(_cached);
      }

      return await FetchUnderGateAsync(ct).ConfigureAwait(false);
    }
    finally
    {
      _ = _gate.Release();
    }
  }

  public void Dispose()
  {
    lock (_refreshLock)
    {
      if (_disposed)
      {
        return;
      }
      _disposed = true;
      _lifetime.Cancel();
      if (_refresh is { IsCompleted: false } refresh)
      {
        _ = refresh.ContinueWith(_ => DisposeResources(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
      }
      else
      {
        DisposeResources();
      }
    }
  }

  private void DisposeResources()
  {
    _gate.Dispose();
    _lifetime.Dispose();
  }

  private bool IsFresh()
      => _cached is not null && DateTimeOffset.UtcNow - _fetchedAt < _config.CatalogCacheTtl;

  /// <summary>Fetches the full catalog and stores it in memory and (when wired) on
  ///     disk. The caller MUST hold <see cref="_gate"/>.</summary>
  private async Task<Result<IReadOnlyList<ModelProviderEntry>>> FetchUnderGateAsync(CancellationToken ct)
  {
    Result<List<IntermediateModel>> phase1 = await FetchModelsAsync(ct).ConfigureAwait(false);
    if (!phase1.IsSuccess)
    {
      return Result.Failure<IReadOnlyList<ModelProviderEntry>>(phase1.Error);
    }

    List<ModelProviderEntry> entries = await ExpandEndpointsAsync(phase1.Value, ct).ConfigureAwait(false);
    _cached = entries;
    _fetchedAt = DateTimeOffset.UtcNow;
    await PersistAsync(ct).ConfigureAwait(false);
    return Result.Success<IReadOnlyList<ModelProviderEntry>>(entries);
  }

  /// <summary>Loads the persisted snapshot into memory when no in-memory copy exists.
  ///     The caller MUST hold <see cref="_gate"/>. A miss or fault leaves the cache
  ///     cold (the caller then fetches); a fault is reported, never thrown.</summary>
  private async Task HydrateFromDiskAsync(CancellationToken ct)
  {
    if (_cache is null || string.IsNullOrEmpty(_cacheKey))
    {
      return;
    }

    // Named decision (CA1031): a cache read is best effort - a fault must never break
    // the catalog, so the cold path simply falls through to a network fetch.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      Result<ModelCatalogSnapshot> stored = await _cache.ReadAsync(_cacheKey, ct).ConfigureAwait(false);
      if (stored.IsSuccess)
      {
        _cached = stored.Value.Entries;
        _fetchedAt = stored.Value.FetchedAt;
      }
    }
    catch (Exception ex)
    {
      ReportFault(ex);
    }
#pragma warning restore CA1031
  }

  /// <summary>Writes the current in-memory catalog to the persisted snapshot. Best
  ///     effort: a storage fault never fails the fetch that produced the data.</summary>
  private async Task PersistAsync(CancellationToken ct)
  {
    if (_cache is null || string.IsNullOrEmpty(_cacheKey) || _cached is null)
    {
      return;
    }

    // Named decision (CA1031): persistence is best effort - the fetched catalog is
    // already in memory and served; a write fault is reported, never thrown.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      _ = await _cache.WriteAsync(_cacheKey, new ModelCatalogSnapshot(_cached, _fetchedAt), ct).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
      ReportFault(ex);
    }
#pragma warning restore CA1031
  }

  /// <summary>Kicks a single background revalidation of a stale catalog. Idempotent:
  ///     a refresh already in flight is never duplicated.</summary>
  private void StartBackgroundRefresh()
  {
    lock (_refreshLock)
    {
      if (_refresh is { IsCompleted: false })
      {
        return;
      }

      _refresh = Task.Run(RevalidateAsync, CancellationToken.None);
    }
  }

  public async Task<Result<IReadOnlyList<ModelProviderEntry>>> RefreshAsync(CancellationToken ct = default)
  {
    Task<Result<IReadOnlyList<ModelProviderEntry>>> refresh;
    lock (_refreshLock)
    {
      ObjectDisposedException.ThrowIf(_disposed, this);
      // Completed refreshes are reusable while fresh, including the disk-hydration
      // refresh that may have finished between cached rendering and this join.
#pragma warning disable CA1849 // Result is read only after IsCompletedSuccessfully, never blocking.
      if (_refresh is null || (_refresh.IsCompleted
          && (!_refresh.IsCompletedSuccessfully || !_refresh.Result.IsSuccess || !IsFresh())))
      {
        _refresh = Task.Run(RevalidateAsync, CancellationToken.None);
      }
#pragma warning restore CA1849
      refresh = _refresh;
    }
    return await refresh.WaitAsync(ct).ConfigureAwait(false);
  }

  private async Task<Result<IReadOnlyList<ModelProviderEntry>>> RevalidateAsync()
  {
    // Named decision (CA1031): a background revalidation fault is best effort - the
    // stale catalog keeps serving and the next call retries; the fault is surfaced
    // through the host callback when one is wired.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
      try
      {
        return await FetchUnderGateAsync(_lifetime.Token).ConfigureAwait(false);
      }
      finally
      {
        _ = _gate.Release();
      }
    }
    catch (Exception ex)
    {
      ReportFault(ex);
      return Result.Failure<IReadOnlyList<ModelProviderEntry>>(new DomainError(
          "CatalogUnavailable", $"Catalog refresh failed: {ex.Message}"));
    }
#pragma warning restore CA1031
  }

  private void ReportFault(Exception ex) => _onRefreshFault?.Invoke(ex);

  private async Task<Result<List<IntermediateModel>>> FetchModelsAsync(CancellationToken ct)
  {
    using HttpRequestMessage request = new(HttpMethod.Get, _config.Endpoint("/api/v1/models"));
    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _config.ApiKey);
    using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);

    if (!response.IsSuccessStatusCode)
    {
      return Result.Failure<List<IntermediateModel>>(new DomainError("CatalogUnavailable",
          $"OpenRouter catalog returned {(int)response.StatusCode}."));
    }

    Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
    JsonDocument doc;
    try
    {
      doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }
    catch (JsonException ex)
    {
      return Result.Failure<List<IntermediateModel>>(new DomainError("CatalogParseError",
          $"Failed to parse catalog JSON: {ex.Message}"));
    }

    using (doc)
    {
      List<IntermediateModel> models = [];
      if (doc.RootElement.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
      {
        foreach (JsonElement item in data.EnumerateArray())
        {
          IntermediateModel? model = ParseModel(item);
          if (model is not null)
          {
            models.Add(model);
          }
        }
      }
      return Result.Success(models);
    }
  }

  private async Task<List<ModelProviderEntry>> ExpandEndpointsAsync(
      List<IntermediateModel> models, CancellationToken ct)
  {
    List<ModelProviderEntry> entries = [];
    await Parallel.ForEachAsync(models, new ParallelOptions
    {
      MaxDegreeOfParallelism = _config.EndpointFetchConcurrency,
      CancellationToken = ct
    },
    async (model, token) =>
    {
      List<ModelProviderEntry> modelEntries = await FetchEndpointsForModelAsync(model, token).ConfigureAwait(false);
      lock (entries)
      {
        entries.AddRange(modelEntries);
      }
    }).ConfigureAwait(false);

    return entries;
  }

  private async Task<List<ModelProviderEntry>> FetchEndpointsForModelAsync(IntermediateModel model, CancellationToken ct)
  {
    using HttpRequestMessage request = new(HttpMethod.Get,
        _config.Endpoint($"/api/v1/models/{model.Id}/endpoints"));
    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _config.ApiKey);

    try
    {
      using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
      if (!response.IsSuccessStatusCode)
      {
        return [CreateFallbackEntry(model)];
      }

      Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
      JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
      using (doc)
      {
        // Two accepted shapes: the legacy top-level array, and the LIVE object
        // envelope {"data": {.., "endpoints": [..]}} (verified against the real
        // API). Anything else — including an empty endpoints array — falls back to
        // the model-level row.
        List<ModelProviderEntry> entries = [];
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
          CollectEndpointEntries(doc.RootElement, model, entries);
        }
        else if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("data", out JsonElement data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("endpoints", out JsonElement endpoints)
            && endpoints.ValueKind == JsonValueKind.Array)
        {
          CollectEndpointEntries(endpoints, model, entries);
        }

        return entries.Count > 0 ? entries : [CreateFallbackEntry(model)];
      }
    }
    catch (HttpRequestException)
    {
      return [CreateFallbackEntry(model)];
    }
    catch (JsonException)
    {
      return [CreateFallbackEntry(model)];
    }
    // OperationCanceledException propagates: a cancelled caller must never be
    // answered with a fallback entry.
  }

  private static IntermediateModel? ParseModel(JsonElement item)
  {
    if (!item.TryGetProperty("id", out JsonElement idEl) || idEl.ValueKind != JsonValueKind.String)
    {
      return null;
    }

    string id = idEl.GetString()!;
    int contextLength = GetIntOr(item, "context_length", 0);
    (decimal promptPrice, decimal completionPrice, decimal discount) = ParsePricing(item);
    bool supportsToolUse = item.TryGetProperty("supported_parameters", out JsonElement paramsEl)
        && paramsEl.ValueKind == JsonValueKind.Array
        && ContainsString(paramsEl, "tools");
    bool supportsVision = ParseSupportsVision(item);
    string? description = GetStringOrNull(item, "description");
    (double? intelligence, double? coding, double? agentic) = ParseScores(item);
    (string? topProviderName, int topProviderContext, int topProviderMaxTokens) =
        ParseTopProvider(item, contextLength);

    IntermediateModel model = new(id, contextLength, promptPrice, completionPrice, discount,
        supportsToolUse, supportsVision, description, intelligence, coding, agentic,
        topProviderName, topProviderContext, topProviderMaxTokens);
    return model;
  }

  /// <summary>Model-level pricing triplet, all zero when pricing is absent or not an object.</summary>
  private static (decimal PromptPrice, decimal CompletionPrice, decimal Discount) ParsePricing(JsonElement item)
  {
    if (item.TryGetProperty("pricing", out JsonElement pricing) && pricing.ValueKind == JsonValueKind.Object)
    {
      return (ParseDecimal(pricing, "prompt"), ParseDecimal(pricing, "completion"), ParseDiscount(pricing));
    }

    return (0m, 0m, 0m);
  }

  /// <summary>Vision capability: any image input modality in the architecture block.
  ///     A MISSING architecture object (or missing modalities) resolves FALSE —
  ///     absence is an answer, never a parse error.</summary>
  private static bool ParseSupportsVision(JsonElement item)
  {
    return item.TryGetProperty("architecture", out JsonElement arch) && arch.ValueKind == JsonValueKind.Object
        && arch.TryGetProperty("input_modalities", out JsonElement modalities) && modalities.ValueKind == JsonValueKind.Array
        && ContainsString(modalities, "image");
  }

  /// <summary>Top-provider display name and limits, falling back to the model-level
  ///     context length and zero completion tokens when absent.</summary>
  private static (string? TopProviderName, int TopProviderContext, int TopProviderMaxTokens) ParseTopProvider(
      JsonElement item, int contextLength)
  {
    if (!item.TryGetProperty("top_provider", out JsonElement tp) || tp.ValueKind != JsonValueKind.Object)
    {
      return (null, contextLength, 0);
    }

    string? topProviderName = GetStringOrNull(tp, "provider_name");
    int topProviderContext = GetIntOr(tp, "context_length", contextLength);
    int topProviderMaxTokens = GetIntOr(tp, "max_completion_tokens", 0);
    return (topProviderName, topProviderContext, topProviderMaxTokens);
  }

  /// <summary>Reads an optional integer property, falling back when absent or not a number.</summary>
  private static int GetIntOr(JsonElement parent, string name, int fallback)
  {
    return parent.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number
        ? el.GetInt32()
        : fallback;
  }

  /// <summary>Reads an optional string property, null when absent or not a string.</summary>
  private static string? GetStringOrNull(JsonElement parent, string name)
  {
    return parent.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String
        ? el.GetString()
        : null;
  }

  /// <summary>Parses one endpoint array's elements into entries; shared by the
  ///     legacy top-level array shape and the live object-envelope shape.</summary>
  private static void CollectEndpointEntries(JsonElement endpoints, IntermediateModel model,
      List<ModelProviderEntry> entries)
  {
    foreach (JsonElement endpoint in endpoints.EnumerateArray())
    {
      ModelProviderEntry? entry = ParseEndpoint(endpoint, model);
      if (entry is not null)
      {
        entries.Add(entry);
      }
    }
  }

  private static ModelProviderEntry? ParseEndpoint(JsonElement endpoint, IntermediateModel model)
  {
    if (!endpoint.TryGetProperty("provider_name", out JsonElement nameEl) || nameEl.ValueKind != JsonValueKind.String)
    {
      return null;
    }

    string providerName = nameEl.GetString()!;
    (int contextLength, int maxCompletionTokens) = ParseEndpointLimits(endpoint, model);
    (decimal promptPrice, decimal completionPrice) = ParseEndpointPrices(endpoint, model);
    (double? latencyMs, double? throughput) = ParseEndpointPerformance(endpoint);

    return new ModelProviderEntry(model.Id, providerName, promptPrice, completionPrice,
        contextLength, maxCompletionTokens, model.SupportsToolUse, model.SupportsVision,
        model.IntelligenceScore, model.CodingScore, model.AgenticScore, latencyMs, throughput,
        model.Description);
  }

  /// <summary>The endpoint row's context window and completion cap: each falls back
  ///     to the model-level value when the endpoint omits it.</summary>
  private static (int ContextLength, int MaxCompletionTokens) ParseEndpointLimits(
      JsonElement endpoint, IntermediateModel model)
      => (OptionalInt(endpoint, "context_length") ?? model.ContextLength,
          OptionalInt(endpoint, "max_completion_tokens") ?? 0);

  /// <summary>The endpoint row's effective prices. The endpoints API's prices ARE
  ///     the effective prices - verified against the live API and the website
  ///     (Baidu $0.35/M served as 0.00000035 with discount: 0.75; the site shows
  ///     $0.35 as the price paid). The discount field is promo metadata, not a
  ///     multiplier: applying it double-discounts. The endpoint row overrides the
  ///     model-level price only when it carries one.</summary>
  private static (decimal PromptPrice, decimal CompletionPrice) ParseEndpointPrices(
      JsonElement endpoint, IntermediateModel model)
  {
    if (!endpoint.TryGetProperty("pricing", out JsonElement pricing) || pricing.ValueKind != JsonValueKind.Object)
    {
      return (model.PromptPrice, model.CompletionPrice);
    }

    return (OverridePositive(ParseDecimal(pricing, "prompt"), model.PromptPrice),
        OverridePositive(ParseDecimal(pricing, "completion"), model.CompletionPrice));
  }

  /// <summary>The endpoint's price override: a price is taken only when the row
  ///     carries a positive one — zero leaves the model-level value untouched.</summary>
  private static decimal OverridePositive(decimal endpointPrice, decimal modelPrice)
      => endpointPrice > 0m ? endpointPrice : modelPrice;

  /// <summary>The endpoint row's observed performance over the trailing 30 days:
  ///     null when the server omits a metric — absence is an answer.</summary>
  private static (double? LatencyMs, double? Throughput) ParseEndpointPerformance(JsonElement endpoint)
      => (OptionalDouble(endpoint, "latency_last_30m"), OptionalDouble(endpoint, "throughput_last_30m"));

  /// <summary>Reads an optional integer property, null when absent or not a number.</summary>
  private static int? OptionalInt(JsonElement parent, string name)
      => parent.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number
          ? el.GetInt32()
          : null;

  /// <summary>Reads an optional double property, null when absent or not a number.</summary>
  private static double? OptionalDouble(JsonElement parent, string name)
      => parent.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number
          ? el.GetDouble()
          : null;
  /// <summary>The row for a model whose per-provider endpoints are unknown (the
  ///     endpoints call failed or the model has none - the routing pseudo-models).
  ///     ProviderName is the top provider when known, else null: a fabricated name
  ///     would ride selection onto the wire as a provider pin ("only": ["Unknown"])
  ///     and fail every request. Null never pins.</summary>
  private static ModelProviderEntry CreateFallbackEntry(IntermediateModel model)
  {
    return new ModelProviderEntry(model.Id, model.TopProviderName, model.PromptPrice, model.CompletionPrice,
        model.TopProviderContext, model.TopProviderMaxTokens, model.SupportsToolUse,
        model.SupportsVision, model.IntelligenceScore, model.CodingScore, model.AgenticScore,
        null, null, model.Description);
  }

  /// <summary>Artificial Analysis indices from the benchmarks block (model level).
  /// The live API serves them at benchmarks.artificial_analysis.*; a missing block
  /// or missing index resolves null - absence is an answer, never a parse error.</summary>
  private static (double? intelligence, double? coding, double? agentic) ParseScores(JsonElement item)
  {
    if (!item.TryGetProperty("benchmarks", out JsonElement benchmarks) || benchmarks.ValueKind != JsonValueKind.Object
        || !benchmarks.TryGetProperty("artificial_analysis", out JsonElement aa) || aa.ValueKind != JsonValueKind.Object)
    {
      return (null, null, null);
    }
    return (ParseScoreDouble(aa, "intelligence_index"), ParseScoreDouble(aa, "coding_index"), ParseScoreDouble(aa, "agentic_index"));
  }

  private static double? ParseScoreDouble(JsonElement parent, string name)
      => parent.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number
          ? el.GetDouble() : null;

  private static decimal ParseDiscount(JsonElement pricing)
  {
    // The live API serves discount as a JSON number; string discounts ride the
    // legacy model-level shape. Both parse; anything else is no discount.
    if (pricing.TryGetProperty("discount", out JsonElement el))
    {
      if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out decimal number))
      {
        return number;
      }

      if (el.ValueKind == JsonValueKind.String
          && decimal.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsed))
      {
        return parsed;
      }
    }

    return 0m;
  }

  private static decimal ParseDecimal(JsonElement parent, string propertyName)
  {
    if (parent.TryGetProperty(propertyName, out JsonElement el) && el.ValueKind == JsonValueKind.String)
    {
      string? s = el.GetString();
      if (decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d))
      {
        return d;
      }
    }
    return 0m;
  }

  private static bool ContainsString(JsonElement array, string value)
  {
    foreach (JsonElement el in array.EnumerateArray())
    {
      if (el.ValueKind == JsonValueKind.String && el.GetString() == value)
      {
        return true;
      }
    }
    return false;
  }

  /// <summary>Phase 1 intermediate: model-level data before per-provider expansion.</summary>
  private sealed record IntermediateModel(
      string Id,
      int ContextLength,
      decimal PromptPrice,
      decimal CompletionPrice,
      decimal Discount,
      bool SupportsToolUse,
      bool SupportsVision,
      string? Description,
      double? IntelligenceScore,
      double? CodingScore,
      double? AgenticScore,
      string? TopProviderName,
      int TopProviderContext,
      int TopProviderMaxTokens);
}
