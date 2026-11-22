using System.Net;
using System.Text.Json;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2213 // _http is injected (DI-owned); only _gate is owned and disposed
namespace eThangAgent.Cloudflare.ACL;

/// <summary>Implements <see cref="IModelCatalog"/> by reading Cloudflare's account-scoped model
///     search endpoint and yielding one entry per chat/text model.
///     <para>THE SEARCH PATH IS NOT THE OPENAI-COMPATIBLE ONE: that path answers
///     method-not-allowed for a model list, so the catalog reads
///     <c>/accounts/{account_id}/ai/models/search</c> on the API host.</para>
///     <para>A FAILED READ IS A NAMED FAILURE AND NEVER AN EMPTY CATALOG. 'Cloudflare serves no
///     chat models' and 'the read failed' are different facts, and a surface that showed the
///     second as the first would tell the user their account has nothing to offer.</para>
///     <para>THE CATALOG IS CACHED in memory for <see cref="CloudflareConfiguration.CatalogCacheTtl"/>
///     and persisted through <see cref="IModelCatalogCache"/> under the provider key
///     <c>cloudflare</c>, so a second read inside the window makes no HTTP call.</para>
///     <para>NO CURATED LIST IS EVER SUBSTITUTED: a static list is a claim about Cloudflare that
///     goes stale silently, so a failed read stays a failed read.</para></summary>
/// <param name="http">The HTTP client every read goes through.</param>
/// <param name="config">The account, token, and host this catalog reads.</param>
/// <param name="cache">The persisted snapshot store, or null for a memory-only catalog.</param>
public sealed class CloudflareModelCatalog(
    HttpClient http,
    CloudflareConfiguration config,
    IModelCatalogCache? cache = null) : IModelCatalog, IDisposable
{
  /// <summary>The provider key this catalog's persisted snapshot is stored under.</summary>
  public const string ProviderKey = ModelVendors.Cloudflare;

  /// <summary>The account-relative path the model search is served on.</summary>
  private const string SearchPath = "ai/models/search";


  private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
  private readonly CloudflareConfiguration _config =
      config ?? throw new ArgumentNullException(nameof(config));
  private readonly IModelCatalogCache? _cache = cache;
  private readonly SemaphoreSlim _gate = new(1, 1);

  /// <summary>The cache key: the provider id AND the account it was read from, so two accounts
  ///     never serve each other's catalog.
  ///     <para>THE KEY IS REBUILT PER READ, not fixed at construction: the credential can be
  ///     replaced live, and a key computed once would keep writing one account's catalog under
  ///     the account the container was built with.</para></summary>
  private string CacheKey => $"{ProviderKey}|{_config.Endpoint(SearchPath).AbsoluteUri}";

  private IReadOnlyList<ModelProviderEntry>? _cached;
  private DateTimeOffset _fetchedAt;

  /// <inheritdoc />
  public async Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default)
  {
    if (!_config.IsConfigured)
    {
      return Result.Failure<IReadOnlyList<ModelProviderEntry>>(Unconfigured());
    }

    if (_cached is not null && IsFresh())
    {
      return Result.Success(_cached);
    }

    await _gate.WaitAsync(ct).ConfigureAwait(false);
    try
    {
      // Named decision (CA1508): the double-check is load-bearing - another caller may have
      // completed the read while this one waited on the gate.
      if (_cached is not null && IsFresh())
      {
        return Result.Success(_cached);
      }

      await HydrateAsync(ct).ConfigureAwait(false);
      return _cached is not null && IsFresh()
          ? Result.Success(_cached)
          : await FetchAsync(ct).ConfigureAwait(false);
    }
    finally
    {
      _ = _gate.Release();
    }
  }

  /// <inheritdoc />
  public async Task<Result<IReadOnlyList<ModelProviderEntry>>> RefreshAsync(
      CancellationToken ct = default)
  {
    if (!_config.IsConfigured)
    {
      return Result.Failure<IReadOnlyList<ModelProviderEntry>>(Unconfigured());
    }

    await _gate.WaitAsync(ct).ConfigureAwait(false);
    try
    {
      return await FetchAsync(ct).ConfigureAwait(false);
    }
    finally
    {
      _ = _gate.Release();
    }
  }

  /// <inheritdoc />
  public async Task<Result<IReadOnlyList<ModelProviderEntry>>> AdoptAsync(
      IReadOnlyList<ModelProviderEntry> entries, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(entries);
    await _gate.WaitAsync(ct).ConfigureAwait(false);
    try
    {
      _cached = entries;
      _fetchedAt = DateTimeOffset.UtcNow;
      await PersistAsync(ct).ConfigureAwait(false);
      return Result.Success(entries);
    }
    finally
    {
      _ = _gate.Release();
    }
  }

  /// <summary>Releases the gate this catalog owns. The injected client is DI-owned and is not
  ///     disposed here.</summary>
  public void Dispose()
  {
    _gate.Dispose();
    GC.SuppressFinalize(this);
  }

  /// <summary>True when the in-memory catalog is inside the configured freshness window.</summary>
  private bool IsFresh()
      => _cached is not null && DateTimeOffset.UtcNow - _fetchedAt < _config.CatalogCacheTtl;

  /// <summary>Reads the persisted snapshot into memory when no in-memory copy exists. A miss or a
  ///     fault leaves the cache cold, and the caller then reads the source.</summary>
  /// <param name="ct">Cancellation.</param>
  private async Task HydrateAsync(CancellationToken ct)
  {
    if (_cache is null || _cached is not null)
    {
      return;
    }

    Result<ModelCatalogSnapshot> stored = await _cache.ReadAsync(CacheKey, ct).ConfigureAwait(false);
    if (stored.IsSuccess)
    {
      _cached = stored.Value.Entries;
      _fetchedAt = stored.Value.FetchedAt;
    }
  }

  /// <summary>Reads the source, translates every chat model, and stores the result in memory and
  ///     (when wired) on disk. The caller MUST hold <see cref="_gate"/>.</summary>
  /// <param name="ct">Cancellation.</param>
  private async Task<Result<IReadOnlyList<ModelProviderEntry>>> FetchAsync(CancellationToken ct)
  {
    Result<List<ModelProviderEntry>> read = await ReadAsync(ct).ConfigureAwait(false);
    if (!read.IsSuccess)
    {
      return Result.Failure<IReadOnlyList<ModelProviderEntry>>(read.Error);
    }

    _cached = read.Value;
    _fetchedAt = DateTimeOffset.UtcNow;
    await PersistAsync(ct).ConfigureAwait(false);
    return Result.Success(_cached);
  }

  /// <summary>One HTTP read of the search endpoint, translated into entries.
  ///     <para>THE BODY IS READ ONCE AND PARSED FROM THE TEXT, so an error response can quote its
  ///     own message rather than a status code alone: Cloudflare's refusals name the fault, and
  ///     that text is what the user needs to see.</para></summary>
  /// <param name="ct">Cancellation.</param>
  private async Task<Result<List<ModelProviderEntry>>> ReadAsync(CancellationToken ct)
  {
    using HttpRequestMessage request = new(HttpMethod.Get, _config.Endpoint(SearchPath));
    request.Headers.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _config.EffectiveApiToken);

    HttpResponseMessage response;
    try
    {
      response = await _http.SendAsync(request, ct).ConfigureAwait(false);
    }
    catch (HttpRequestException ex)
    {
      return Result.Failure<List<ModelProviderEntry>>(new DomainError("CatalogUnavailable",
          $"the Cloudflare model catalog could not be reached: {ex.Message}"));
    }

    using (response)
    {
      string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
      return response.IsSuccessStatusCode
          ? Parse(body)
          : Result.Failure<List<ModelProviderEntry>>(StatusFailure(response.StatusCode, body));
    }
  }

  /// <summary>The named failure an absent credential earns. A container can outlive the
  ///     credential it was built with, and a read that names no account is a configuration fact
  ///     rather than a transport fault.</summary>
  private static DomainError Unconfigured() => new("ProviderNotConfigured",
      "Cloudflare needs both an account id and an API token before its models can be read.");

  /// <summary>The named failure a non-success status earns.
  ///     <para>A REFUSED CREDENTIAL IS ITS OWN CODE: 401 and 403 mean the token was not accepted,
  ///     which is a configuration fault the user fixes, and it must be distinguishable from a
  ///     network fault the user waits out.</para></summary>
  /// <param name="status">The status the response carried.</param>
  /// <param name="body">The response body, which carries Cloudflare's own message.</param>
  private static DomainError StatusFailure(HttpStatusCode status, string body)
  {
    string detail = CloudflareErrorText.Of(body);
    return status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
        ? new DomainError("ProviderUnauthorized",
            $"Cloudflare refused the credential ({(int)status}): {detail}")
        : new DomainError("CatalogUnavailable",
            $"the Cloudflare model catalog returned {(int)status}: {detail}");
  }

  /// <summary>The entries a successful response describes: one per chat model, in the order the
  ///     response lists them.
  ///     <para>NON-CHAT MODELS ARE EXCLUDED BEFORE ANY ENTRY IS PRODUCED, so nothing downstream
  ///     ever sees an image generator or an embedding model.</para>
  ///     <para>A BODY THAT IS NOT THE DOCUMENTED ENVELOPE IS A NAMED FAILURE, never an empty
  ///     catalog: a response this catalog cannot read is a read that failed.</para></summary>
  /// <param name="body">The response body.</param>
  private static Result<List<ModelProviderEntry>> Parse(string body)
  {
    try
    {
      using JsonDocument document = JsonDocument.Parse(body);
      return document.RootElement.ValueKind == JsonValueKind.Object
          && document.RootElement.TryGetProperty("result", out JsonElement result)
          && result.ValueKind == JsonValueKind.Array
              ? Result.Success(Entries(result))
              : Result.Failure<List<ModelProviderEntry>>(new DomainError("CatalogParseError",
                  "the Cloudflare model catalog response carries no 'result' array."));
    }
    catch (JsonException ex)
    {
      return Result.Failure<List<ModelProviderEntry>>(new DomainError("CatalogParseError",
          $"the Cloudflare model catalog response is not valid JSON: {ex.Message}"));
    }
  }

  /// <summary>The chat models among a response's rows.</summary>
  /// <param name="result">The response's <c>result</c> array.</param>
  private static List<ModelProviderEntry> Entries(JsonElement result)
  {
    List<ModelProviderEntry> entries = [];
    foreach (JsonElement model in result.EnumerateArray())
    {
      if (!CloudflareModelFacts.IsChatModel(TaskName(model)))
      {
        continue;
      }

      if (CloudflareModelFacts.ToEntry(model) is { } entry)
      {
        entries.Add(entry);
      }
    }

    return entries;
  }

  /// <summary>The task name a row states, or null when it states none.
  ///     <para>A ROW THAT NAMES NO TASK IS NOT A CHAT MODEL: this catalog admits only what
  ///     Cloudflare labels as text generation, so silence is excluded rather than assumed.</para>
  ///     </summary>
  /// <param name="model">One element of the response's <c>result</c> array.</param>
  private static string? TaskName(JsonElement model)
      => model.ValueKind == JsonValueKind.Object
          && model.TryGetProperty("task", out JsonElement task)
          && task.ValueKind == JsonValueKind.Object
          && task.TryGetProperty("name", out JsonElement name)
          && name.ValueKind == JsonValueKind.String
              ? name.GetString()
              : null;

  /// <summary>Writes the current catalog to the persisted snapshot. A storage fault never fails
  ///     the read that produced the data.</summary>
  /// <param name="ct">Cancellation.</param>
  private async Task PersistAsync(CancellationToken ct)
  {
    if (_cache is null || _cached is null)
    {
      return;
    }

    _ = await _cache.WriteAsync(CacheKey,
        new ModelCatalogSnapshot(_cached, _fetchedAt, ModelCatalogSnapshot.CurrentShapeVersion),
        ct).ConfigureAwait(false);
  }
}
