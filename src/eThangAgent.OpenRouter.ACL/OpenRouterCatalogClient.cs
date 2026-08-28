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
public sealed class OpenRouterCatalogClient(HttpClient http, OpenRouterConfiguration config) : IModelCatalog, IDisposable
{
  private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
  private readonly OpenRouterConfiguration _config = config ?? throw new ArgumentNullException(nameof(config));
  private readonly SemaphoreSlim _gate = new(1, 1);
  private IReadOnlyList<ModelProviderEntry>? _cached;
  private DateTimeOffset _fetchedAt;

  public async Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default)
  {
    if (IsFresh())
    {
      return Result.Success(_cached!);
    }

    await _gate.WaitAsync(ct).ConfigureAwait(false);
    try
    {
      if (IsFresh())
      {
        return Result.Success(_cached!);
      }

      Result<List<IntermediateModel>> phase1 = await FetchModelsAsync(ct).ConfigureAwait(false);
      if (!phase1.IsSuccess)
      {
        return Result.Failure<IReadOnlyList<ModelProviderEntry>>(phase1.Error!);
      }

      List<ModelProviderEntry> entries = await ExpandEndpointsAsync(phase1.Value!, ct).ConfigureAwait(false);
      _cached = entries;
      _fetchedAt = DateTimeOffset.UtcNow;
      return Result.Success<IReadOnlyList<ModelProviderEntry>>(entries);
    }
    finally
    {
      _ = _gate.Release();
    }
  }

  public void Dispose() => _gate.Dispose();

  private bool IsFresh()
      => _cached is not null && DateTimeOffset.UtcNow - _fetchedAt < _config.CatalogCacheTtl;

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
        List<ModelProviderEntry> entries = [];
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
          foreach (JsonElement endpoint in doc.RootElement.EnumerateArray())
          {
            ModelProviderEntry? entry = ParseEndpoint(endpoint, model);
            if (entry is not null)
            {
              entries.Add(entry);
            }
          }
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

  /// <summary>Vision capability: any image input modality in the architecture block.</summary>
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

  private static ModelProviderEntry? ParseEndpoint(JsonElement endpoint, IntermediateModel model)
  {
    if (!endpoint.TryGetProperty("provider_name", out JsonElement nameEl) || nameEl.ValueKind != JsonValueKind.String)
    {
      return null;
    }

    string providerName = nameEl.GetString()!;
    int contextLength = endpoint.TryGetProperty("context_length", out JsonElement ctx) && ctx.ValueKind == JsonValueKind.Number
        ? ctx.GetInt32() : model.ContextLength;
    int maxCompletionTokens = endpoint.TryGetProperty("max_completion_tokens", out JsonElement mct) && mct.ValueKind == JsonValueKind.Number
        ? mct.GetInt32() : 0;

    decimal promptPrice = model.PromptPrice;
    decimal completionPrice = model.CompletionPrice;
    decimal discount = model.Discount;
    if (endpoint.TryGetProperty("pricing", out JsonElement pricing) && pricing.ValueKind == JsonValueKind.Object)
    {
      decimal ep = ParseDecimal(pricing, "prompt");
      decimal ec = ParseDecimal(pricing, "completion");
      if (ep > 0m || ec > 0m)
      {
        promptPrice = ep;
        completionPrice = ec;
      }
      discount = ParseDiscount(pricing);
    }

    decimal effectivePrompt = EffectivePrice(promptPrice, discount);
    decimal effectiveCompletion = EffectivePrice(completionPrice, discount);

    double? latencyMs = endpoint.TryGetProperty("latency_ms", out JsonElement lat) && lat.ValueKind == JsonValueKind.Number
        ? lat.GetDouble() : null;
    double? throughput = endpoint.TryGetProperty("throughput_tokens_per_second", out JsonElement thr) && thr.ValueKind == JsonValueKind.Number
        ? thr.GetDouble() : null;

    return new ModelProviderEntry(model.Id, providerName, effectivePrompt, effectiveCompletion,
        contextLength, maxCompletionTokens, model.SupportsToolUse, model.SupportsVision,
        model.IntelligenceScore, model.CodingScore, model.AgenticScore, latencyMs, throughput,
        model.Description);
  }

  private static ModelProviderEntry CreateFallbackEntry(IntermediateModel model)
  {
    decimal effectivePrompt = EffectivePrice(model.PromptPrice, model.Discount);
    decimal effectiveCompletion = EffectivePrice(model.CompletionPrice, model.Discount);
    string providerName = model.TopProviderName ?? "Unknown";
    return new ModelProviderEntry(model.Id, providerName, effectivePrompt, effectiveCompletion,
        model.TopProviderContext, model.TopProviderMaxTokens, model.SupportsToolUse,
        model.SupportsVision, model.IntelligenceScore, model.CodingScore, model.AgenticScore,
        null, null, model.Description);
  }

  private static (double? intelligence, double? coding, double? agentic) ParseScores(JsonElement item)
  {
    if (!item.TryGetProperty("scores", out JsonElement scores) || scores.ValueKind != JsonValueKind.Object)
    {
      return (null, null, null);
    }
    return (ParseScoreDouble(scores, "intelligence"), ParseScoreDouble(scores, "coding"), ParseScoreDouble(scores, "agentic"));
  }

  private static double? ParseScoreDouble(JsonElement parent, string name)
      => parent.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number
          ? el.GetDouble() : null;

  private static decimal ParseDiscount(JsonElement pricing)
  {
    if (pricing.TryGetProperty("discount", out JsonElement el) && el.ValueKind == JsonValueKind.String)
    {
      string? s = el.GetString();
      if (decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d))
      {
        return d;
      }
    }
    return 0m;
  }

  private static decimal EffectivePrice(decimal basePrice, decimal discount)
      => basePrice * (1m - discount);

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
