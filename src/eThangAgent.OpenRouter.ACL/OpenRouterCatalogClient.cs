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
  private Dictionary<string, PublishedProviderStats> _published = new(StringComparer.Ordinal);
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
    // The published rates are fetched AFTER the endpoints, because binding a rate to a row
    // needs that row's key. They are best effort by construction: a model that publishes
    // nothing simply contributes nothing.
    Dictionary<string, PublishedProviderStats> published =
        await FetchPublishedStatsAsync(phase1.Value, ct).ConfigureAwait(false);
    _cached = entries;
    _published = published;
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

  /// <summary>The published per-endpoint cache-hit rates, keyed by
  ///     <see cref="ModelProviderEntry.Key"/>. Empty until a fetch has run, and empty again
  ///     when the fetch found nothing - a state the resolution chain already handles.</summary>
  public Task<IReadOnlyDictionary<string, PublishedProviderStats>> GetPublishedStatsAsync(
      CancellationToken ct = default)
  {
    lock (_refreshLock)
    {
      return Task.FromResult<IReadOnlyDictionary<string, PublishedProviderStats>>(
          _published);
    }
  }

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

    return DeduplicateByEndpoint(entries);
  }

  /// <summary>One row per ROUTABLE endpoint (issue #296 follow-up).
  ///     <para>THE CATALOG'S CONTRACT IS 'ONE ROW PER ENDPOINT', and the variant slug is what
  ///     an endpoint is. The API occasionally serves the same tag twice with different prices -
  ///     measured live 2026-10-27, <c>google-vertex/us-south1</c> appeared twice on
  ///     <c>qwen/qwen3-235b-a22b-2507</c> at $0.22/M and $0.25/M - and that is an UPSTREAM
  ///     DUPLICATE, not two products: <c>provider.only</c> can only name the tag, so the router
  ///     reaches one endpoint and the two prices are the API disagreeing with itself. Keeping
  ///     both would show the user two rows they cannot choose between.</para>
  ///     <para>THE CHEAPEST ADVERTISED PRICE WINS, deterministically: when the provider states
  ///     two prices for one routable target, the lower one is the claim we can act on, and
  ///     choosing by price (rather than by arrival order, which parallel fetching makes
  ///     arbitrary) keeps the catalog stable across runs. A row with no advertised price sorts
  ///     last, so an unpriced duplicate never displaces a priced one.</para>
  ///     <para>THIS IS NOT THE COLLAPSING THE VARIANT KEY EXISTS TO PREVENT: that collapsed
  ///     DIFFERENT endpoints (region, ZDR, tier) onto a provider name. This merges rows that
  ///     name the SAME routable endpoint.</para></summary>
  private static List<ModelProviderEntry> DeduplicateByEndpoint(List<ModelProviderEntry> entries)
  {
    Dictionary<string, ModelProviderEntry> byKey = new(entries.Count, StringComparer.Ordinal);
    foreach (ModelProviderEntry entry in entries)
    {
      if (!byKey.TryGetValue(entry.Key, out ModelProviderEntry? existing)
          || CheaperPromptPrice(entry, existing) < 0)
      {
        byKey[entry.Key] = entry;
      }
    }

    return [.. byKey.Values];
  }

  /// <summary>Negative when <paramref name="candidate"/> advertises a lower prompt price than
  ///     <paramref name="current"/>; positive when higher; zero when equal or unknowable.
  ///     An absent price sorts last, because an unpriced row cannot be shown to be cheaper.
  ///     </summary>
  private static int CheaperPromptPrice(ModelProviderEntry candidate, ModelProviderEntry current)
      => (candidate.PromptPricePerToken, current.PromptPricePerToken) switch
      {
        ({ } left, { } right) => left.CompareTo(right),
        ({ }, null) => -1,
        (null, { }) => 1,
        _ => 0,
      };

  /// <summary>Fetches the cache-hit rates providers publish for each model (issue #296
  ///     follow-up), keyed by <see cref="ModelProviderEntry.Key"/> so a rate binds to exactly
  ///     the row the comparison prices.
  ///     <para>WHY PER MODEL AND NOT PER ENDPOINT: the published statistics are served one
  ///     model at a time (there is no bulk form - verified against the live API), and the
  ///     response already carries every endpoint of that model, so one call covers a model's
  ///     whole provider list.</para>
  ///     <para>BEST EFFORT, ALWAYS. These are undocumented frontend endpoints: a shape change,
  ///     a 404, or a network fault must leave the picker working with listed prices and the
  ///     documented default rather than failing the catalog. Every failure resolves to 'no
  ///     published rate for this model', which is a state the resolution chain already
  ///     handles.</para></summary>
  private async Task<Dictionary<string, PublishedProviderStats>> FetchPublishedStatsAsync(
      List<IntermediateModel> models, CancellationToken ct)
  {
    Dictionary<string, PublishedProviderStats> stats = new(StringComparer.Ordinal);
    await Parallel.ForEachAsync(models, new ParallelOptions
    {
      MaxDegreeOfParallelism = _config.EndpointFetchConcurrency,
      CancellationToken = ct
    },
    async (model, token) =>
    {
      Dictionary<string, PublishedProviderStats> modelStats =
          await FetchPublishedStatsForModelAsync(model, token).ConfigureAwait(false);
      lock (stats)
      {
        foreach (KeyValuePair<string, PublishedProviderStats> pair in modelStats)
        {
          stats[pair.Key] = pair.Value;
        }
      }
    }).ConfigureAwait(false);

    return stats;
  }

  /// <summary>One model's published per-endpoint rates, keyed by the same composite the
  ///     catalog rows use. An empty result means the model published nothing - never a
  ///     failure the caller must handle.</summary>
  private async Task<Dictionary<string, PublishedProviderStats>> FetchPublishedStatsForModelAsync(
      IntermediateModel model, CancellationToken ct)
  {
    Dictionary<string, PublishedProviderStats> stats = new(StringComparer.Ordinal);
    if (string.IsNullOrEmpty(model.Permaslug))
    {
      return stats;
    }

    // Named decision (CA1031): the published statistics are an ENRICHMENT. A fault here must
    // never fail a catalog the picker can otherwise render, so every failure resolves to
    // 'nothing published' and the resolution chain falls through to the documented default.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      // TWO CALLS, DELIBERATELY (measured live 2026-10-27). The endpoint payload carries the
      // EXACT provider names the catalog uses (30/30 and 5/5 matched, zero missing), while
      // the effective-pricing summaries carry display names with de-duplication suffixes
      // ("DeepInfra (2)") that match only 20 of 35. The endpoint payload has no rates and the
      // summaries have no exact names, so the two are joined on endpointId - which matched
      // 35/35 - and the result is keyed by the catalog's own model+name pair.
      using HttpRequestMessage endpointsRequest = new(HttpMethod.Get, _config.EndpointWithQuery(
          "/api/frontend/v1/stats/endpoint",
          $"permaslug={Uri.EscapeDataString(model.Permaslug)}&variant=standard"));
      using HttpResponseMessage endpointsResponse =
          await _http.SendAsync(endpointsRequest, ct).ConfigureAwait(false);
      if (!endpointsResponse.IsSuccessStatusCode)
      {
        return stats;
      }

      using HttpRequestMessage pricingRequest = new(HttpMethod.Get, _config.EndpointWithQuery(
          "/api/frontend/v1/stats/effective-pricing",
          $"permaslug={Uri.EscapeDataString(model.Permaslug)}"));
      using HttpResponseMessage pricingResponse =
          await _http.SendAsync(pricingRequest, ct).ConfigureAwait(false);
      if (!pricingResponse.IsSuccessStatusCode)
      {
        return stats;
      }

      JsonDocument endpointsDoc = await JsonDocument.ParseAsync(
          await endpointsResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
          cancellationToken: ct).ConfigureAwait(false);
      JsonDocument pricingDoc = await JsonDocument.ParseAsync(
          await pricingResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
          cancellationToken: ct).ConfigureAwait(false);
      using (endpointsDoc)
      using (pricingDoc)
      {
        CollectPublishedStats(endpointsDoc.RootElement, pricingDoc.RootElement, model, stats);
      }
    }
    catch (Exception ex) when (ex is HttpRequestException or JsonException)
    {
      return stats;
    }
#pragma warning restore CA1031

    return stats;
  }

  /// <summary>Joins the two published payloads into one rate per catalog pair.
  ///     <para>WHY TWO DOCUMENTS: the endpoint payload carries the EXACT provider names the
  ///     catalog uses (measured live 2026-10-27: 30/30 and 5/5 matched, zero missing) but no
  ///     rates; the effective-pricing payload carries the rates but names providers with
  ///     de-duplication suffixes ("DeepInfra (2)") that match only 20 of 35 catalog rows.
  ///     The two share <c>endpointId</c>, which joined 35/35, so the id is the join key and
  ///     the name comes from the endpoint payload.</para>
  ///     <para>WHERE SEVERAL ENDPOINTS SHARE A NAME the one with the most traffic behind its
  ///     figure wins. The catalog's pair key is model + provider NAME and cannot distinguish
  ///     them either - it renders one row per name - so the best-evidenced figure is the only
  ///     defensible choice. Volume is deterministic; arrival order would let a 34K-token
  ///     endpoint outrank a 9.5B-token one for no reason.</para></summary>
  private static void CollectPublishedStats(JsonElement endpointsRoot, JsonElement pricingRoot,
      IntermediateModel model, Dictionary<string, PublishedProviderStats> stats)
  {
    if (PublishedEndpoints(endpointsRoot) is not { } endpoints
        || ProviderSummaries(pricingRoot) is not { } summaries)
    {
      return;
    }

    Dictionary<string, string> keysById = CatalogKeysByEndpointId(endpoints, model);
    foreach (JsonElement summary in summaries)
    {
      if (summary.ValueKind == JsonValueKind.Object
          && GetStringOrNull(summary, "endpointId") is { } endpointId
          && keysById.TryGetValue(endpointId, out string? key))
      {
        Adopt(stats, key, summary);
      }
    }
  }

  /// <summary>The catalog pair each published endpoint belongs to, keyed by the endpoint id
  ///     the two payloads share.
  ///     <para>THE KEY INCLUDES THE VARIANT SLUG, exactly as
  ///     <see cref="ModelProviderEntry.Key"/> does (issue #296 follow-up). The published
  ///     statistics are served per ENDPOINT, so a name-only key would bind one endpoint's
  ///     rate to every endpoint of that provider - and the endpoints measured live differ
  ///     hugely (one model's seven ranged 0% to 74.5%), so that is the single-number
  ///     projection the per-pair resolution exists to remove. The endpoint payload carries the
  ///     same <c>tag</c> the catalog does, so the two join on it.</para></summary>
  private static Dictionary<string, string> CatalogKeysByEndpointId(JsonElement[] endpoints,
      IntermediateModel model)
  {
    Dictionary<string, string> keysById = new(StringComparer.Ordinal);
    foreach (JsonElement endpoint in endpoints)
    {
      if (endpoint.ValueKind == JsonValueKind.Object
          && GetStringOrNull(endpoint, "id") is { } id
          && GetStringOrNull(endpoint, "provider_name") is { } providerName)
      {
        EndpointVariant variant = EndpointVariant.Parse(
            GetStringOrNull(endpoint, "tag"),
            GetStringOrNull(endpoint, "service_tier"),
            GetStringOrNull(endpoint, "quantization"));
        keysById[id] = variant.Slug is { } slug
            ? $"{model.Id}:{providerName}:{slug}"
            : $"{model.Id}:{providerName}";
      }
    }

    return keysById;
  }

  /// <summary>Records one published summary for a pair, keeping the figure with the most
  ///     traffic behind it when several endpoints share a name.</summary>
  private static void Adopt(Dictionary<string, PublishedProviderStats> stats, string key,
      JsonElement summary)
  {
    PublishedProviderStats candidate = PublishedProviderStats.Create(
        ParseHitRate(summary),
        ParsePricePerToken(summary, "effectiveInputPrice"),
        ParsePricePerToken(summary, "effectiveOutputPrice"),
        ParseTokenCount(summary));

    if (!stats.TryGetValue(key, out PublishedProviderStats? existing)
        || (candidate.TotalTokens ?? 0) > (existing.TotalTokens ?? 0))
    {
      stats[key] = candidate;
    }
  }

  /// <summary>The endpoint array from the published endpoint payload, or null when the shape
  ///     is not one we understand.</summary>
  private static JsonElement[]? PublishedEndpoints(JsonElement root)
      => root.ValueKind == JsonValueKind.Object
          && root.TryGetProperty("data", out JsonElement data)
          && data.ValueKind == JsonValueKind.Array
              ? [.. data.EnumerateArray()]
              : null;

  /// <summary>The provider summaries array from one model's statistics payload, or null when
  ///     the payload is not the shape we understand. Extracted so the walk above stays a
  ///     single readable loop rather than a chain of shape guards.</summary>
  private static JsonElement[]? ProviderSummaries(JsonElement root)
      => root.ValueKind == JsonValueKind.Object
          && root.TryGetProperty("data", out JsonElement data)
          && data.ValueKind == JsonValueKind.Object
          && data.TryGetProperty("providerSummaries", out JsonElement summaries)
          && summaries.ValueKind == JsonValueKind.Array
              ? [.. summaries.EnumerateArray()]
              : null;

  /// <summary>The published cache-hit share, or null when absent or out of range. A rate
  ///     outside [0,1] is a payload we do not understand, and refusing it keeps a bad parse
  ///     from becoming a plausible-looking price.</summary>
  private static decimal? ParseHitRate(JsonElement summary)
      => summary.TryGetProperty("cacheHitRate", out JsonElement el)
          && el.ValueKind == JsonValueKind.Number
          && el.TryGetDecimal(out decimal rate)
          && rate is >= 0m and <= 1m
              ? rate
              : null;

  /// <summary>A published per-MILLION price converted to per-token, or null when absent. The
  ///     statistics payload serves dollars per million; the catalog serves dollars per token,
  ///     and the two must not be mixed in one formula.</summary>
  private static decimal? ParsePricePerToken(JsonElement summary, string name)
      => summary.TryGetProperty(name, out JsonElement el)
          && el.ValueKind == JsonValueKind.Number
          && el.TryGetDecimal(out decimal perMillion)
          && perMillion >= 0m
              ? perMillion / 1_000_000m
              : null;

  /// <summary>The token volume the published figures rest on, or null when absent.</summary>
  private static long? ParseTokenCount(JsonElement summary)
      => summary.TryGetProperty("totalTokens", out JsonElement el)
          && el.ValueKind == JsonValueKind.Number
          && el.TryGetInt64(out long tokens)
              ? tokens
              : null;

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
    (decimal? promptPrice, decimal? cacheReadPrice, decimal? completionPrice, decimal discount) = ParsePricing(item);
    (decimal? cacheWritePrice, List<PriceOverride>? overrides) =
        item.TryGetProperty("pricing", out JsonElement modelPricing) && modelPricing.ValueKind == JsonValueKind.Object
            ? (ParsePriceOrNull(modelPricing, CacheWritePriceField), ParseOverrides(modelPricing))
            : (null, null);
    bool supportsToolUse = item.TryGetProperty("supported_parameters", out JsonElement paramsEl)
        && paramsEl.ValueKind == JsonValueKind.Array
        && ContainsString(paramsEl, "tools");
    bool supportsVision = ParseSupportsVision(item);
    bool supportsTextOutput = ParseSupportsTextOutput(item);
    bool isRouter = ParseIsRouter(item);
    string? description = GetStringOrNull(item, "description");
    (double? intelligence, double? coding, double? agentic) = ParseScores(item);
    (string? topProviderName, int topProviderContext, int topProviderMaxTokens) =
        ParseTopProvider(item, contextLength);

    IntermediateModel model = new(id, contextLength, promptPrice, cacheReadPrice, completionPrice, discount,
        supportsToolUse, supportsVision, supportsTextOutput, isRouter, description, intelligence, coding,
        agentic, topProviderName, topProviderContext, topProviderMaxTokens, cacheWritePrice, overrides)
    {
      Permaslug = GetStringOrNull(item, "canonical_slug"),
    };
    return model;
  }

  /// <summary>Model-level pricing, with EVERY price nullable (issue #289). A missing
  ///     pricing block, a missing field, and unparseable text all resolve NULL - unknown -
  ///     never zero: the old default of 0 made an unpriced model indistinguishable from a
  ///     genuinely free one, which is exactly the distinction a free-model selector needs.
  ///     A served "0" is still a real price of zero.</summary>
  private static (decimal? PromptPrice, decimal? CacheReadPrice, decimal? CompletionPrice, decimal Discount) ParsePricing(
      JsonElement item)
  {
    if (item.TryGetProperty("pricing", out JsonElement pricing) && pricing.ValueKind == JsonValueKind.Object)
    {
      return (ParsePriceOrNull(pricing, "prompt"), ParsePriceOrNull(pricing, CacheReadPriceField),
          ParsePriceOrNull(pricing, "completion"), ParseDiscount(pricing));
    }

    return (null, null, null, 0m);
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

  /// <summary>Text-output capability (issue #289): "text" among the architecture
  ///     block's output modalities. A MISSING architecture object resolves FALSE - a model
  ///     that declared no output modality has not declared text, and capability is never
  ///     guessed. A generator that also emits audio (text+audio) still resolves true: it
  ///     DOES produce text, and it is rejected elsewhere for its missing tool capability.</summary>
  private static bool ParseSupportsTextOutput(JsonElement item)
  {
    return item.TryGetProperty("architecture", out JsonElement arch) && arch.ValueKind == JsonValueKind.Object
        && arch.TryGetProperty("output_modalities", out JsonElement modalities) && modalities.ValueKind == JsonValueKind.Array
        && ContainsString(modalities, "text");
  }

  /// <summary>Whether this row is a ROUTER rather than a model (issue #289): the
  ///     architecture block's tokenizer reads "Router" for the pseudo-models that pick an
  ///     upstream per request (openrouter/auto, openrouter/free, the ~-prefixed "latest"
  ///     aliases), and measured on the live catalog that flag is exactly right - 27 rows
  ///     carry it, and the four pseudo-models whose endpoints are unknown (auto, free,
  ///     fusion, bodybuilder) are among them. An id-prefix test cannot express this: the
  ///     ~-aliases route without the openrouter/ prefix, and a provider namespace may
  ///     legitimately contain such a prefix.
  ///     <para>Nothing is inferred when the block is absent: a row that declares no
  ///     tokenizer is treated as a model, because calling a real model a router would
  ///     silently remove it from every selection.</para></summary>
  private static bool ParseIsRouter(JsonElement item)
  {
    return item.TryGetProperty("architecture", out JsonElement arch) && arch.ValueKind == JsonValueKind.Object
        && arch.TryGetProperty("tokenizer", out JsonElement tokenizer)
        && tokenizer.ValueKind == JsonValueKind.String
        && string.Equals(tokenizer.GetString(), "Router", StringComparison.Ordinal);
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
    (decimal? promptPrice, decimal? cacheReadPrice, decimal? completionPrice) = ParseEndpointPrices(endpoint, model);
    (double? latencyMs, double? throughput) = ParseEndpointPerformance(endpoint);
    (double? uptime30m, double? uptime5m, double? uptime1d) = ParseEndpointUptime(endpoint);
    bool? implicitCaching = ParseImplicitCaching(endpoint);
    (decimal? cacheWritePrice, IReadOnlyList<PriceOverride>? overrides) = ParseEndpointCacheAndOverrides(endpoint, model);

    return new ModelProviderEntry(model.Id, providerName, promptPrice, cacheReadPrice, completionPrice,
        contextLength, maxCompletionTokens, model.SupportsToolUse, model.SupportsVision,
        model.SupportsTextOutput, model.IntelligenceScore, model.CodingScore, model.AgenticScore,
        latencyMs, throughput, model.Description, model.IsRouter,
        uptime30m, uptime5m, uptime1d, implicitCaching, cacheWritePrice, overrides)
    {
      // The variant is what makes this endpoint distinct from the provider's other endpoints
      // (issue #296 follow-up): the tag is the routable slug, and service_tier/quantization
      // describe how it differs. Read here so every entry carries its own identity rather
      // than sharing the provider's.
      Variant = EndpointVariant.Parse(
          GetStringOrNull(endpoint, "tag"),
          GetStringOrNull(endpoint, "service_tier"),
          GetStringOrNull(endpoint, "quantization")),
    };
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
  private static (decimal? PromptPrice, decimal? CacheReadPrice, decimal? CompletionPrice) ParseEndpointPrices(
      JsonElement endpoint, IntermediateModel model)
  {
    if (!endpoint.TryGetProperty("pricing", out JsonElement pricing) || pricing.ValueKind != JsonValueKind.Object)
    {
      return (model.PromptPrice, model.CacheReadPrice, model.CompletionPrice);
    }

    return (OverrideAdvertisedPrice(ParsePriceOrNull(pricing, "prompt"), model.PromptPrice),
        OverrideCacheReadPrice(ParsePriceOrNull(pricing, CacheReadPriceField), model.CacheReadPrice),
        OverrideAdvertisedPrice(ParsePriceOrNull(pricing, "completion"), model.CompletionPrice));
  }

  /// <summary>The endpoint's cache-read price override: taken only when the row carries a
  ///     non-negative one. A negative advertised value (-1 on the routing pseudo-models)
  ///     means "set at routing time", never a price, so it leaves the model-level value
  ///     untouched; an absent endpoint value inherits the model-level one.</summary>
  private static decimal? OverrideCacheReadPrice(decimal? endpointPrice, decimal? modelPrice)
      => endpointPrice is { } price && price >= 0m ? price : modelPrice;

  /// <summary>The endpoint's prompt/completion price override: a POSITIVE advertised price
  ///     wins (state the endpoint's own number when it has one), and anything else - absent,
  ///     unparseable, negative, or zero - inherits the model-level value. Zero inheriting is
  ///     deliberate: a zero on an endpoint row carries no information beyond "no separate
  ///     price here", and the model-level row is the authority for a free model either way.
  ///     A null model-level value stays null - absence inherits absence.</summary>
  private static decimal? OverrideAdvertisedPrice(decimal? endpointPrice, decimal? modelPrice)
      => endpointPrice is { } price && price > 0m ? price : modelPrice;

  /// <summary>The endpoint row's advertised latency and throughput: null when the server
  ///     omits a metric — absence is an answer.
  ///     <para>MEASURED DEAD, 2026-10-27 (issue #293): across 5 models and 52 endpoints fetched
  ///     from the live endpoints API, <c>latency_last_30m</c> and <c>throughput_last_30m</c>
  ///     were null on EVERY row. The fields are still parsed — the provider may populate them
  ///     later, and a parser that stopped reading them could never notice — but no consumer
  ///     treats them as a latency source. <see cref="ProviderPerformanceText"/> is what states
  ///     an absent number as absent rather than as zero.</para></summary>
  private static (double? LatencyMs, double? Throughput) ParseEndpointPerformance(JsonElement endpoint)
      => (OptionalDouble(endpoint, "latency_last_30m"), OptionalDouble(endpoint, "throughput_last_30m"));

  /// <summary>The endpoint's served-request success rates over the trailing 30 minutes, 5
  ///     minutes, and 1 day (issue #293, D2), each null when the server omits it.
  ///     <para>THIS IS THE RELIABILITY SIGNAL THAT IS ACTUALLY POPULATED: measured live, one
  ///     model served it on 35 of 35 endpoints while two others served it on 3 of 7 and 6 of 8.
  ///     A provider at 87% uptime is a real risk to a long session in a way no price column can
  ///     express, and before this it was parsed by nothing.</para></summary>
  private static (double? Uptime30m, double? Uptime5m, double? Uptime1d) ParseEndpointUptime(JsonElement endpoint)
      => (OptionalDouble(endpoint, "uptime_last_30m"), OptionalDouble(endpoint, "uptime_last_5m"),
          OptionalDouble(endpoint, "uptime_last_1d"));

  /// <summary>Whether the endpoint caches prompts implicitly (issue #293, D3), or NULL when it
  ///     did not say.
  ///     <para>THE TRI-STATE IS THE POINT: a row loaded from a catalog cache written before this
  ///     member existed has said NOTHING, and reading that as false would tell the user a
  ///     caching provider does not cache. False is only ever a parsed false; the field is
  ///     present on every live endpoint today, so in practice this resolves a real boolean.</para>
  ///     <para>Why it matters beyond a label: a row that advertises a cache-read price but does
  ///     not support implicit caching will never actually charge it, so the picker can now
  ///     distinguish "cheap cache reads" from "cheap cache reads you will never get".</para></summary>
  private static bool? ParseImplicitCaching(JsonElement endpoint)
      => endpoint.TryGetProperty("supports_implicit_caching", out JsonElement el)
          && el.ValueKind is JsonValueKind.True or JsonValueKind.False
              ? el.GetBoolean()
              : null;

  /// <summary>The endpoint's cache-WRITE price and its price ladder (issue #293, D4/D5), both
  ///     read from the endpoint's own pricing block.
  ///     <para>Both fall back to the MODEL-level pricing block when the endpoint omits them: the
  ///     live /api/v1/models payload serves <c>input_cache_write</c> and <c>overrides</c> at the
  ///     model level for models whose endpoints repeat them, so a reader that only looked at the
  ///     endpoint row would miss half of them.</para></summary>
  private static (decimal? CacheWritePrice, List<PriceOverride>? Overrides) ParseEndpointCacheAndOverrides(
      JsonElement endpoint, IntermediateModel model)
  {
    JsonElement pricing = endpoint.TryGetProperty("pricing", out JsonElement own) && own.ValueKind == JsonValueKind.Object
        ? own
        : default;
    return (ParsePriceOrNull(pricing, CacheWritePriceField) ?? model.CacheWritePrice,
        ParseOverrides(pricing) ?? model.PriceOverrides);
  }

  /// <summary>The price ladder under <c>pricing.overrides</c> (issue #293), or null when the
  ///     endpoint serves a single price.
  ///     <para>THE SHAPE IS A PROMPT-SIZE TIER, verified against the live payload: every entry
  ///     carries <c>min_prompt_tokens</c> and a full price set, and none carries a time bound.
  ///     An entry with no <c>min_prompt_tokens</c> is SKIPPED rather than defaulted to zero —
  ///     a rung that applies to every call would silently replace the base price with whatever
  ///     that entry happened to say.</para></summary>
  private static List<PriceOverride>? ParseOverrides(JsonElement pricing)
  {
    if (pricing.ValueKind != JsonValueKind.Object
        || !pricing.TryGetProperty("overrides", out JsonElement overrides)
        || overrides.ValueKind != JsonValueKind.Array)
    {
      return null;
    }

    List<PriceOverride> tiers = [];
    foreach (JsonElement tier in overrides.EnumerateArray())
    {
      if (tier.ValueKind != JsonValueKind.Object || OptionalInt(tier, "min_prompt_tokens") is not { } minTokens)
      {
        continue;
      }

      tiers.Add(new PriceOverride(minTokens, ParsePriceOrNull(tier, "prompt"),
          ParsePriceOrNull(tier, CacheReadPriceField), ParsePriceOrNull(tier, CacheWritePriceField),
          ParsePriceOrNull(tier, "completion")));
    }

    return tiers.Count > 0 ? tiers : null;
  }

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
    // The model-level ladder and cache-write price still apply here: they come from the
    // /api/v1/models payload, which succeeded even though this model's endpoints call did not.
    // Everything endpoint-specific stays null - SupportsImplicitCaching included, because an
    // unparsed endpoint has not said false (issue #293).
    return new ModelProviderEntry(model.Id, model.TopProviderName, model.PromptPrice, model.CacheReadPrice,
        model.CompletionPrice,
        model.TopProviderContext, model.TopProviderMaxTokens, model.SupportsToolUse,
        model.SupportsVision, model.SupportsTextOutput, model.IntelligenceScore, model.CodingScore,
        model.AgenticScore, null, null, model.Description, model.IsRouter, PriceOverrides: model.PriceOverrides,
        PromptCacheWritePricePerToken: model.CacheWritePrice);
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

  /// <summary>The endpoints API's cache-read price field name: what the provider charges
  ///     for an input token served from the prompt cache (issue #22).</summary>
  private const string CacheReadPriceField = "input_cache_read";

  /// <summary>The endpoints API's cache-WRITE price field name: what the provider charges to
  ///     put a prompt INTO its cache (issue #293, D5). Cache writes are billed on some providers
  ///     and were invisible, so a model whose cache reads are free but whose cache writes cost
  ///     1.25x the prompt price read as a bargain it is not.</summary>
  private const string CacheWritePriceField = "input_cache_write";

  /// <summary>Reads an optional price: null when the property is absent, not a string, or
  ///     not a parseable decimal. A negative advertised value is likewise not a price (the
  ///     routing pseudo-models serve -1 to mean "set at routing time"), while a served
  ///     zero IS a price - cache reads billed at nothing.</summary>
  private static decimal? ParsePriceOrNull(JsonElement parent, string propertyName)
      => parent.TryGetProperty(propertyName, out JsonElement el) && el.ValueKind == JsonValueKind.String
          && decimal.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsed)
          && parsed >= 0m
              ? parsed
              : null;

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

  /// <summary>Phase 1 intermediate: model-level data before per-provider expansion.
  ///     <para><c>Permaslug</c> is the model's canonical slug, the key the published
  ///     statistics are served under. Read from <c>canonical_slug</c>: verified live
  ///     2026-10-27 against 464 models, it matched the frontend catalog's <c>permaslug</c>
  ///     exactly on all 376 that had a catalog row and disagreed on none. Null when the API
  ///     omitted it, in which case no published rate is fetched for the model.</para>
  ///     </summary>
  private sealed record IntermediateModel(
      string Id,
      int ContextLength,
      decimal? PromptPrice,
      decimal? CacheReadPrice,
      decimal? CompletionPrice,
      decimal Discount,
      bool SupportsToolUse,
      bool SupportsVision,
      bool SupportsTextOutput,
      bool IsRouter,
      string? Description,
      double? IntelligenceScore,
      double? CodingScore,
      double? AgenticScore,
      string? TopProviderName,
      int TopProviderContext,
      int TopProviderMaxTokens,
      decimal? CacheWritePrice,
      List<PriceOverride>? PriceOverrides)
  {
    /// <summary>The model's canonical slug, the key the published statistics are served
    ///     under, or null when the API omitted it.</summary>
    public string? Permaslug { get; init; }
  }
}
