using System.Net.Http.Json;
using System.Text.Json;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Local.ACL;

/// <summary>Implements <see cref="IModelCatalog"/> against an OpenAI-compatible local
///     server's own lineup: <c>GET /v1/models</c> lists the served model ids in order,
///     and each model's context window resolves through native probes with a floor
///     fallback — LM Studio's batch <c>GET /api/v0/models</c> (one call answering every
///     listed id) first, then Ollama's per-model <c>POST /api/show</c>, then
///     <see cref="DefaultContextFloor"/>. A reported context_length of 0, absent, or
///     non-numeric counts as no answer, and every probe failure (HTTP error, connection
///     failure, malformed JSON, timeout) fails soft into the next tier; only the lineup
///     itself failing or coming back empty fails the catalog. The resolved list is
///     cached in a field after the first success (failures are never cached): the lineup
///     is a session-lifetime snapshot of what the server advertised at first fetch,
///     matching the cached-catalog precedent — the server's own listing stays the
///     authority for this ACL. Entries are shaped per the
///     <c>ZaiModelCatalog</c> precedent for catalogs without provider-published
///     economics: prices are free, tool use on, vision off, scores/latency/throughput
///     null — the description carries the signal.</summary>
public sealed class LocalModelCatalog(HttpClient http, LocalConfiguration config) : IModelCatalog
{
  /// <summary>Serving-provider name stamped on every entry; also the exclusion-key
  ///     discriminator, chosen so it cannot collide with OpenRouter upstream names.</summary>
  public const string ProviderName = "local";

  /// <summary>Context window used when no probe answers. Deliberately small: a model
  ///     that outgrows it should surface that fact through a working probe, not through
  ///     an inflated guess.</summary>
  public const int DefaultContextFloor = 4096;

  private const string ProviderUnreachable = "ProviderUnreachable";

  private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
  private readonly LocalConfiguration _config = config ?? throw new ArgumentNullException(nameof(config));
  private IReadOnlyList<ModelProviderEntry>? _cached;

  public async Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default)
  {
    if (_cached is { } cached)
    {
      return Result.Success(cached);
    }

    Result<List<string>> lineup = await FetchLineupAsync(ct).ConfigureAwait(false);
    if (!lineup.IsSuccess)
    {
      return Result.Failure<IReadOnlyList<ModelProviderEntry>>(lineup.Error);
    }

    Dictionary<string, int> lmStudioWindows = await ProbeLmStudioAsync(ct).ConfigureAwait(false);
    List<ModelProviderEntry> entries = [];
    foreach (string id in lineup.Value)
    {
      int window = lmStudioWindows.TryGetValue(id, out int batchWindow)
          ? batchWindow
          : await ProbeOllamaAsync(id, ct).ConfigureAwait(false) ?? DefaultContextFloor;
      entries.Add(new ModelProviderEntry(
          id, ProviderName, 0m, 0m,
          window,
          // Local servers advertise no separate completion cap: the context window IS
          // the completion ceiling, so both properties carry the probed value.
          window,
          SupportsToolUse: true, SupportsVision: false,
          IntelligenceScore: null, CodingScore: null, AgenticScore: null,
          LatencyMs: null, ThroughputTokensPerSec: null,
          $"Local model served by an OpenAI-compatible server at {_config.BaseUrl.Host}."));
    }

    _cached = entries;
    return Result.Success<IReadOnlyList<ModelProviderEntry>>(entries);
  }

  /// <summary>The catalog's own first entry's model id — the bootstrap default when no
  ///     model choice is persisted — or the catalog's failure (unreachable server or
  ///     empty catalog).</summary>
  public async Task<Result<string>> FirstModelIdAsync(CancellationToken ct = default)
  {
    Result<IReadOnlyList<ModelProviderEntry>> catalog = await GetAsync(ct).ConfigureAwait(false);
    return catalog.Match(
        entries => entries.Count > 0
            ? Result.Success(entries[0].ModelId)
            : Result.Failure<string>(new DomainError(ProviderUnreachable,
                $"The local server at {_config.BaseUrl.Host} lists no models.")),
        Result.Failure<string>);
  }

  /// <summary>Fetches and parses <c>GET /v1/models</c> into the served ids, in listing
  ///     order. This is the one call whose failure fails the catalog: the ACL has no
  ///     lineup without it. A non-success status, transport failure, or unparseable body
  ///     all mean the same thing — the server's lineup is unreachable — so they share the
  ///     <see cref="ProviderUnreachable"/> code (a deliberately cancelled probe surfaces
  ///     as a failure the same way; the operation did not complete).</summary>
  private async Task<Result<List<string>>> FetchLineupAsync(CancellationToken ct)
  {
    JsonElement root;
    try
    {
      using HttpRequestMessage request = new(HttpMethod.Get, _config.ModelsEndpoint());
      AddAuth(request);
      using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
      if (!response.IsSuccessStatusCode)
      {
        return LineupFailure($"The local server returned HTTP {(int)response.StatusCode} for its model list.");
      }

      root = await response.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
    }
    catch (HttpRequestException ex)
    {
      return LineupFailure($"The local server is unreachable: {ex.Message}");
    }
    catch (TaskCanceledException)
    {
      return LineupFailure("The local server did not answer its model list in time.");
    }
    catch (JsonException ex)
    {
      return LineupFailure($"The local server's model list is not valid JSON: {ex.Message}");
    }

    List<string> ids = [];
    if (root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("data", out JsonElement data)
        && data.ValueKind == JsonValueKind.Array)
    {
      foreach (JsonElement item in data.EnumerateArray())
      {
        if (item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty("id", out JsonElement id)
            && id.ValueKind == JsonValueKind.String)
        {
          ids.Add(id.GetString()!);
        }
      }
    }

    return ids.Count == 0
        ? LineupFailure($"The local server at {_config.BaseUrl.Host} lists no models.")
        : Result.Success(ids);
  }

  /// <summary>One batch probe of LM Studio's native listing: an id → context_length map
  ///     for every entry reporting a positive numeric one. Any failure — non-success
  ///     status, transport error, timeout, malformed JSON — is no answer from this tier
  ///     and fails soft into the empty map, so the per-model probe takes over.</summary>
  private async Task<Dictionary<string, int>> ProbeLmStudioAsync(CancellationToken ct)
  {
    Dictionary<string, int> windows = [];
    try
    {
      using HttpRequestMessage request = new(HttpMethod.Get, _config.LmStudioModelsEndpoint());
      AddAuth(request);
      using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
      if (!response.IsSuccessStatusCode)
      {
        return windows;
      }

      JsonElement root = await response.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
      if (root.ValueKind == JsonValueKind.Object
          && root.TryGetProperty("data", out JsonElement data)
          && data.ValueKind == JsonValueKind.Array)
      {
        foreach (JsonElement item in data.EnumerateArray())
        {
          if (item.ValueKind == JsonValueKind.Object
              && item.TryGetProperty("id", out JsonElement id)
              && id.ValueKind == JsonValueKind.String
              && TryPositiveContext(item, out int window))
          {
            windows[id.GetString()!] = window;
          }
        }
      }
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
    {
      return windows; // soft: this tier has no answer; the per-model probe falls through.
    }

    return windows;
  }

  /// <summary>Ollama's per-model native probe: <c>POST /api/show</c> with exactly the
  ///     listed id. Any failure is no answer from this tier — the caller applies the
  ///     floor.</summary>
  private async Task<int?> ProbeOllamaAsync(string id, CancellationToken ct)
  {
    try
    {
      using HttpRequestMessage request = new(HttpMethod.Post, _config.OllamaShowEndpoint())
      {
        Content = JsonContent.Create(new Dictionary<string, object?> { ["model"] = id }),
      };
      AddAuth(request);
      using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
      if (!response.IsSuccessStatusCode)
      {
        return null;
      }

      JsonElement root = await response.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
      return root.ValueKind == JsonValueKind.Object && TryPositiveContext(root, out int window)
          ? window
          : null;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
    {
      return null; // soft: the floor answers instead.
    }
  }

  /// <summary>Reads an entry's context_length: present, numeric, and strictly positive.
  ///     A 0, absent, or non-numeric report is no answer — the tier falls through, never
  ///     a zero window.</summary>
  private static bool TryPositiveContext(JsonElement entry, out int contextLength)
  {
    contextLength = 0;
    return entry.TryGetProperty("context_length", out JsonElement value)
        && value.TryGetInt32(out contextLength)
        && contextLength > 0;
  }

  private static Result<List<string>> LineupFailure(string detail)
      => Result.Failure<List<string>>(new DomainError(ProviderUnreachable, detail));

  private void AddAuth(HttpRequestMessage request)
  {
    if (_config.ApiKey is { } apiKey)
    {
      request.Headers.Authorization = new("Bearer", apiKey);
    }
  }
}
