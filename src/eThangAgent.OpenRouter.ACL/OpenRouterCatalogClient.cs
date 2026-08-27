using System.Text.Json;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2213 // _http is injected (DI-owned); only _gate is owned and disposed
namespace eThangAgent.OpenRouter.ACL;

/// <summary>Implements <see cref="IModelCatalog"/> by fetching OpenRouter's /api/v1/models
/// endpoint and caching the result in-memory with a configurable TTL. Thread-safe via
/// SemaphoreSlim to prevent stampede on cache expiry.</summary>
public sealed class OpenRouterCatalogClient(HttpClient http, OpenRouterConfiguration config) : IModelCatalog, IDisposable
{
  private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
  private readonly OpenRouterConfiguration _config = config ?? throw new ArgumentNullException(nameof(config));
  private readonly SemaphoreSlim _gate = new(1, 1);
  private IReadOnlyList<ModelCatalogEntry>? _cached;
  private DateTimeOffset _fetchedAt;

  public async Task<Result<IReadOnlyList<ModelCatalogEntry>>> GetAsync(CancellationToken ct = default)
  {
    if (IsFresh())
    {
      return Result.Success(_cached!);
    }

    await _gate.WaitAsync(ct).ConfigureAwait(false);
    try
    {
      // Double-check after acquiring the lock; another caller may have refreshed.
      if (IsFresh())
      {
        return Result.Success(_cached!);
      }

      Uri uri = new(_config.BaseUrl, "/api/v1/models");
      using HttpRequestMessage request = new(HttpMethod.Get, uri);
      request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _config.ApiKey);
      using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);

      if (!response.IsSuccessStatusCode)
      {
        return Result.Failure<IReadOnlyList<ModelCatalogEntry>>(
            new DomainError("CatalogUnavailable", $"OpenRouter catalog returned {(int)response.StatusCode}."));
      }

      Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
      JsonDocument doc;
      try
      {
        doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
      }
      catch (JsonException ex)
      {
        return Result.Failure<IReadOnlyList<ModelCatalogEntry>>(
            new DomainError("CatalogParseError", $"Failed to parse catalog JSON: {ex.Message}"));
      }

      using (doc)
      {
        List<ModelCatalogEntry> entries = [];
        if (doc.RootElement.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
        {
          foreach (JsonElement item in data.EnumerateArray())
          {
            ModelCatalogEntry? entry = ParseEntry(item);
            if (entry is not null)
            {
              entries.Add(entry);
            }
          }
        }

        _cached = entries;
        _fetchedAt = DateTimeOffset.UtcNow;
        return Result.Success<IReadOnlyList<ModelCatalogEntry>>(entries);
      }
    }
    finally
    {
      _ = _gate.Release();
    }
  }

  public void Dispose() => _gate.Dispose();

  private bool IsFresh()
      => _cached is not null && DateTimeOffset.UtcNow - _fetchedAt < _config.CatalogCacheTtl;

  private static ModelCatalogEntry? ParseEntry(JsonElement item)
  {
    if (!item.TryGetProperty("id", out JsonElement idEl) || idEl.ValueKind != JsonValueKind.String)
    {
      return null;
    }

    string id = idEl.GetString()!;
    int contextLength = item.TryGetProperty("context_length", out JsonElement ctx) && ctx.ValueKind == JsonValueKind.Number
        ? ctx.GetInt32() : 0;

    decimal promptPrice = 0m;
    decimal completionPrice = 0m;
    if (item.TryGetProperty("pricing", out JsonElement pricing) && pricing.ValueKind == JsonValueKind.Object)
    {
      promptPrice = ParseDecimal(pricing, "prompt");
      completionPrice = ParseDecimal(pricing, "completion");
    }

    bool supportsToolUse = item.TryGetProperty("supported_parameters", out JsonElement paramsEl)
        && paramsEl.ValueKind == JsonValueKind.Array
        && ContainsString(paramsEl, "tools");

    bool supportsVision = false;
    if (item.TryGetProperty("architecture", out JsonElement arch) && arch.ValueKind == JsonValueKind.Object
        && arch.TryGetProperty("input_modalities", out JsonElement modalities) && modalities.ValueKind == JsonValueKind.Array)
    {
      supportsVision = ContainsString(modalities, "image");
    }

    string? description = item.TryGetProperty("description", out JsonElement desc) && desc.ValueKind == JsonValueKind.String
        ? desc.GetString() : null;

    return new ModelCatalogEntry(id, promptPrice, completionPrice, contextLength,
        supportsToolUse, supportsVision, null, description);
  }

  private static decimal ParseDecimal(JsonElement parent, string propertyName)
  {
    if (parent.TryGetProperty(propertyName, out JsonElement el) && el.ValueKind == JsonValueKind.String)
    {
      string? s = el.GetString();
      if (decimal.TryParse(s, System.Globalization.NumberStyles.Float,
          System.Globalization.CultureInfo.InvariantCulture, out decimal d))
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
}