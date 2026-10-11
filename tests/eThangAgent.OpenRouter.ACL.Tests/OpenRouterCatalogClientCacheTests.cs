using System.Net;
using System.Text;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2007 // test code does not need ConfigureAwait
#pragma warning disable CA2000 // HttpClient owns the handler; test lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

/// <summary>In-memory stand-in for the persisted catalog cache: seeds snapshots, records
///     writes, and reports misses with the same named error the SQLite store uses.</summary>
internal sealed class FakeModelCatalogCache : IModelCatalogCache
{
  private readonly Dictionary<string, ModelCatalogSnapshot> _store = new(StringComparer.Ordinal);

  public int WriteCount { get; private set; }

  public void Seed(string provider, ModelCatalogSnapshot snapshot) => _store[provider] = snapshot;

  public Task<Result<ModelCatalogSnapshot>> ReadAsync(string provider, CancellationToken ct = default)
      => Task.FromResult(_store.TryGetValue(provider, out ModelCatalogSnapshot? s)
          ? Result.Success(s)
          : Result.Failure<ModelCatalogSnapshot>(new DomainError("CatalogCacheMiss", "none stored")));

  public Task<Result<bool>> WriteAsync(string provider, ModelCatalogSnapshot snapshot, CancellationToken ct = default)
  {
    _store[provider] = snapshot;
    WriteCount++;
    return Task.FromResult(Result.Success(true));
  }
}

/// <summary>Stale-while-revalidate behavior of the catalog client: a warm copy is served
///     at once and refreshed in the background; a cold cache hydrates from the persisted
///     snapshot before touching the network; a network fetch persists the result.</summary>
public class OpenRouterCatalogClientCacheTests
{
  private static readonly Uri BaseUrl = new("https://openrouter.test");

  private const string ModelsJson =
      "{\"data\":[{\"id\":\"google/gemini-2.0-flash-001\",\"context_length\":1048576,"
      + "\"pricing\":{\"prompt\":\"0.000001\",\"completion\":\"0.000002\"},"
      + "\"architecture\":{\"input_modalities\":[\"text\"]},\"supported_parameters\":[\"tools\"],"
      + "\"top_provider\":{\"context_length\":1048576,\"max_completion_tokens\":8192}}]}";

  private const string EndpointsJson =
      "[{\"provider_name\":\"Google\",\"context_length\":1048576,\"max_completion_tokens\":8192,"
      + "\"pricing\":{\"prompt\":\"0.000001\",\"completion\":\"0.000002\"}}]";

  private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
      new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

  private static HttpResponseMessage Route(HttpRequestMessage req) =>
      req.RequestUri!.AbsolutePath switch
      {
        "/api/v1/models" => Json(HttpStatusCode.OK, ModelsJson),
        "/api/v1/models/google/gemini-2.0-flash-001/endpoints" => Json(HttpStatusCode.OK, EndpointsJson),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound)
      };

  private static ModelProviderEntry PersistedEntry() =>
      new("persisted/model", "Persisted", 0.000009m, 0.000010m, 200_000, 4096, true, false, 90.0, 85.0, 80.0, null, null, "from disk");

  [Fact]
  public async Task GetAsync_ColdCache_ServesPersistedSnapshot_WithoutHttp()
  {
    FakeModelCatalogCache cache = new();
    cache.Seed("openrouter|https://openrouter.test/api/v1/models", new ModelCatalogSnapshot([PersistedEntry()], DateTimeOffset.UtcNow));
    int httpCalls = 0;
    FakeHttpMessageHandler handler = new(req =>
    {
      _ = Interlocked.Increment(ref httpCalls);
      return Task.FromResult(Route(req));
    });
    using HttpClient http = new(handler);
    OpenRouterCatalogClient client = new(http, new OpenRouterConfiguration("k", BaseUrl), cache, "openrouter");

    Result<IReadOnlyList<ModelProviderEntry>> result = await client.GetAsync(TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    ModelProviderEntry entry = Assert.Single(result.Value);
    Assert.Equal("persisted/model", entry.ModelId);
    Assert.Equal(0, httpCalls); // served entirely from disk
  }

  [Fact]
  public async Task GetAsync_ColdCacheNoSnapshot_FetchesAndPersists()
  {
    FakeModelCatalogCache cache = new();
    FakeHttpMessageHandler handler = new(req => Task.FromResult(Route(req)));
    using HttpClient http = new(handler);
    OpenRouterCatalogClient client = new(http, new OpenRouterConfiguration("k", BaseUrl), cache, "openrouter");

    Result<IReadOnlyList<ModelProviderEntry>> result = await client.GetAsync(TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("google/gemini-2.0-flash-001", Assert.Single(result.Value).ModelId);
    Assert.Equal(1, cache.WriteCount); // the fetched catalog was persisted
  }

  [Fact]
  public async Task GetAsync_WarmFreshCache_ServesWithoutHttpOrRefresh()
  {
    FakeModelCatalogCache cache = new();
    cache.Seed("openrouter|https://openrouter.test/api/v1/models", new ModelCatalogSnapshot([PersistedEntry()], DateTimeOffset.UtcNow));
    int httpCalls = 0;
    FakeHttpMessageHandler handler = new(req =>
    {
      _ = Interlocked.Increment(ref httpCalls);
      return Task.FromResult(Route(req));
    });
    using HttpClient http = new(handler);
    OpenRouterCatalogClient client = new(http, new OpenRouterConfiguration("k", BaseUrl), cache, "openrouter");

    _ = await client.GetAsync(TestContext.Current.CancellationToken); // hydrates memory from disk
    _ = await client.GetAsync(TestContext.Current.CancellationToken); // warm + fresh

    Assert.Equal(0, httpCalls);
    Assert.Null(client.PendingRefresh); // fresh: no revalidation kicked
  }

  [Fact]
  public async Task GetAsync_WarmStaleCache_ServesStaleImmediately_ThenRevalidatesInBackground()
  {
    // TTL zero makes every in-memory copy stale, so the second call exercises the
    // stale-while-revalidate path deterministically. No persisted seed: the first
    // call fetches from the network and populates memory.
    FakeModelCatalogCache cache = new();
    TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int modelsCalls = 0;
    FakeHttpMessageHandler handler = new(req =>
    {
      if (req.RequestUri!.AbsolutePath == "/api/v1/models")
      {
        int n = Interlocked.Increment(ref modelsCalls);
        // The initial fetch (call 1) answers at once; the background revalidation
        // (call 2) parks on the gate so the test can prove the stale serve did not wait.
        return n == 1 ? Task.FromResult(Route(req)) : HoldThenRespond(gate.Task, req);
      }
      return Task.FromResult(Route(req));
    });
    using HttpClient http = new(handler);
    OpenRouterCatalogClient client = new(http, new OpenRouterConfiguration("k", BaseUrl) { CatalogCacheTtl = TimeSpan.Zero }, cache, "openrouter");

    _ = await client.GetAsync(TestContext.Current.CancellationToken); // cold fetch -> memory populated

    Result<IReadOnlyList<ModelProviderEntry>> stale = await client.GetAsync(TestContext.Current.CancellationToken);
    Assert.True(stale.IsSuccess);
    Assert.Equal("google/gemini-2.0-flash-001", Assert.Single(stale.Value).ModelId); // served the warm copy
    Task? refresh = client.PendingRefresh;
    Assert.NotNull(refresh); // a background revalidation was kicked
    Assert.False(refresh.IsCompleted); // still parked on the gate: the stale serve did not wait

    gate.SetResult(); // release the revalidation
    await refresh;
    Assert.Equal(2, modelsCalls); // the background refresh ran to completion
  }

  [Fact]
  public async Task GetAsync_LegacyProviderOnlySnapshot_DoesNotServeUnidentifiedSource()
  {
    FakeModelCatalogCache cache = new();
    cache.Seed("openrouter", new ModelCatalogSnapshot([PersistedEntry()], DateTimeOffset.UtcNow));
    FakeHttpMessageHandler handler = new(req => Task.FromResult(Route(req)));
    using HttpClient http = new(handler);
    using OpenRouterCatalogClient client = new(http, new OpenRouterConfiguration("k", BaseUrl), cache, "openrouter");
    Result<IReadOnlyList<ModelProviderEntry>> result = await client.GetAsync(TestContext.Current.CancellationToken);
    Assert.True(result.IsSuccess);
    Assert.Equal("google/gemini-2.0-flash-001", Assert.Single(result.Value).ModelId);
    Assert.Equal(1, cache.WriteCount);
  }

  [Fact]
  public async Task GetAsync_DifferentApiSource_DoesNotReuseAnotherSourcesSnapshot()
  {
    FakeModelCatalogCache cache = new();
    FakeHttpMessageHandler firstHandler = new(req => Task.FromResult(Route(req)));
    using HttpClient firstHttp = new(firstHandler);
    using OpenRouterCatalogClient first = new(firstHttp, new OpenRouterConfiguration("k", BaseUrl), cache, "openrouter");
    _ = await first.GetAsync(TestContext.Current.CancellationToken);
    int calls = 0;
    FakeHttpMessageHandler secondHandler = new(req =>
    {
      _ = Interlocked.Increment(ref calls);
      return Task.FromResult(Route(req));
    });
    using HttpClient secondHttp = new(secondHandler);
    using OpenRouterCatalogClient second = new(secondHttp,
        new OpenRouterConfiguration("k", new Uri("https://another-source.test")), cache, "openrouter");
    Result<IReadOnlyList<ModelProviderEntry>> result = await second.GetAsync(TestContext.Current.CancellationToken);
    Assert.True(result.IsSuccess);
    Assert.Equal(2, calls);
    Assert.Equal(2, cache.WriteCount);
  }

  [Fact]
  public async Task GetAsync_SameApiSource_NewClientReusesSnapshot()
  {
    FakeModelCatalogCache cache = new();
    FakeHttpMessageHandler firstHandler = new(req => Task.FromResult(Route(req)));
    using HttpClient firstHttp = new(firstHandler);
    using OpenRouterCatalogClient first = new(firstHttp, new OpenRouterConfiguration("k", BaseUrl), cache, "openrouter");
    _ = await first.GetAsync(TestContext.Current.CancellationToken);
    FakeHttpMessageHandler secondHandler = new(_ => throw new InvalidOperationException("fresh snapshot should avoid HTTP"));
    using HttpClient secondHttp = new(secondHandler);
    using OpenRouterCatalogClient second = new(secondHttp,
        new OpenRouterConfiguration("different-key", BaseUrl), cache, "openrouter");
    Result<IReadOnlyList<ModelProviderEntry>> result = await second.GetAsync(TestContext.Current.CancellationToken);
    Assert.True(result.IsSuccess);
    Assert.Equal("google/gemini-2.0-flash-001", Assert.Single(result.Value).ModelId);
    Assert.Equal(1, cache.WriteCount);
  }
  private static async Task<HttpResponseMessage> HoldThenRespond(Task gate, HttpRequestMessage req)
  {
    await gate;
    return Route(req);
  }
}
