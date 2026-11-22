using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2007 // test code does not need ConfigureAwait
#pragma warning disable CA2000 // HttpClient owns the handler; test lifetime bounds it

namespace eThangAgent.Cloudflare.ACL.Tests;

/// <summary>In-memory stand-in for the persisted catalog cache: seeds snapshots, records the
///     provider key each write used, and reports a miss with the same named error the SQLite store
///     uses.</summary>
internal sealed class FakeModelCatalogCache : IModelCatalogCache
{
  private readonly Dictionary<string, ModelCatalogSnapshot> _store = [with(StringComparer.Ordinal)];

  /// <summary>The provider keys this cache was written under, in order.</summary>
  public List<string> WrittenKeys { get; } = [];

  public void Seed(string provider, ModelCatalogSnapshot snapshot) => _store[provider] = snapshot;

  public Task<Result<ModelCatalogSnapshot>> ReadAsync(string provider, CancellationToken ct = default)
      => Task.FromResult(_store.TryGetValue(provider, out ModelCatalogSnapshot? snapshot)
          ? Result.Success(snapshot)
          : Result.Failure<ModelCatalogSnapshot>(new DomainError("CatalogCacheMiss", "none stored")));

  public Task<Result<bool>> WriteAsync(string provider, ModelCatalogSnapshot snapshot,
      CancellationToken ct = default)
  {
    _store[provider] = snapshot;
    WrittenKeys.Add(provider);
    return Task.FromResult(Result.Success(true));
  }
}

/// <summary>The catalog's persistence: one snapshot per account, written under the provider key
///     <c>cloudflare</c>, and a cold process that hydrates it without a network call.
///     </summary>
public class CloudflareModelCatalogCacheTests
{
  private const string AccountId = "acct-123";

  private static CloudflareConfiguration Configuration() => new(AccountId, "token");

  private static string Response()
      => /*lang=json,strict*/
          """{"success":true,"errors":[],"result":[{"name":"@cf/a/b","task":{"name":"Text Generation"},"properties":[]}]}""";

  [Fact]
  public async Task ARead_PersistsItsSnapshotUnderTheCloudflareProviderKey()
  {
    FakeModelCatalogCache cache = new();
    using CloudflareModelCatalog catalog =
        new(new HttpClient(FakeHttpMessageHandler.Json(Response())), Configuration(), cache);

    _ = await catalog.GetAsync(TestContext.Current.CancellationToken);

    string key = Assert.Single(cache.WrittenKeys);
    Assert.StartsWith("cloudflare|", key, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AColdProcess_HydratesThePersistedSnapshotWithoutAnHttpCall()
  {
    FakeModelCatalogCache cache = new();
    FakeHttpMessageHandler handler = FakeHttpMessageHandler.Json(Response());
    using (CloudflareModelCatalog first =
        new(new HttpClient(handler), Configuration(), cache))
    {
      _ = await first.GetAsync(TestContext.Current.CancellationToken);
    }

    FakeHttpMessageHandler secondHandler = FakeHttpMessageHandler.Json(Response());
    using CloudflareModelCatalog second =
        new(new HttpClient(secondHandler), Configuration(), cache);
    Result<IReadOnlyList<ModelProviderEntry>> read =
        await second.GetAsync(TestContext.Current.CancellationToken);

    Assert.True(read.IsSuccess, read.Error?.Message);
    Assert.Equal("@cf/a/b", Assert.Single(read.Value).ModelId);
    Assert.Equal(0, secondHandler.CallCount);
  }

  [Fact]
  public async Task TwoAccounts_DoNotServeEachOthersCatalog()
  {
    FakeModelCatalogCache cache = new();
    using CloudflareModelCatalog one =
        new(new HttpClient(FakeHttpMessageHandler.Json(Response())), Configuration(), cache);
    using CloudflareModelCatalog two = new(
        new HttpClient(FakeHttpMessageHandler.Json(Response())),
        new CloudflareConfiguration("acct-other", "token"),
        cache);

    _ = await one.GetAsync(TestContext.Current.CancellationToken);
    _ = await two.GetAsync(TestContext.Current.CancellationToken);

    Assert.Equal(2, cache.WrittenKeys.Count);
    Assert.NotEqual(cache.WrittenKeys[0], cache.WrittenKeys[1]);
  }

  [Fact]
  public async Task Refresh_ReadsTheSourceAgain()
  {
    FakeHttpMessageHandler handler = FakeHttpMessageHandler.Json(Response());
    using CloudflareModelCatalog catalog = new(new HttpClient(handler), Configuration());

    _ = await catalog.GetAsync(TestContext.Current.CancellationToken);
    _ = await catalog.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.Equal(2, handler.CallCount);
  }

  [Fact]
  public async Task TheCatalogDoesNotInventStatisticsOrARetentionList()
  {
    // Both reads keep the interface's own defaults: Cloudflare publishes neither, so the catalog
    // states nothing rather than an empty list it never read.
    using CloudflareModelCatalog catalog =
        new(new HttpClient(FakeHttpMessageHandler.Json(Response())), Configuration());
    IModelCatalog seam = catalog;

    Assert.Empty(await seam.GetPublishedStatsAsync(TestContext.Current.CancellationToken));
    Assert.Null(await seam.GetRetentionEligibleAsync(TestContext.Current.CancellationToken));
  }
}
