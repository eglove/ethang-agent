using System.Net;
using System.Text;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;

namespace eThangAgent.Composition.Tests;

/// <summary>The catalog probe a settings surface uses to prove a credential and fill the model
///     search in one action.
///     <para>THE PULL IS PERSISTED UNDER THE KEY THE SESSION'S OWN CATALOG HYDRATES FROM, which
///     is what makes a tested credential show its models without restarting the app. The tests
///     read that key back rather than trusting the probe's own report.</para>
///     <para>Nothing here reaches a network: the client is a fake handler, and every assertion
///     is about the request that was made or the snapshot that was written.</para></summary>
public class ProviderCatalogProbeTests
{
  private const string CloudflareBody =
      "{\"result\":[{\"name\":\"@cf/meta/llama-3.1-8b-instruct\",\"task\":{\"name\":\"Text Generation\"}},"
      + "{\"name\":\"@cf/baai/bge-small\",\"task\":{\"name\":\"Text Embeddings\"}}]}";

  private const string OpenRouterBody =
      "{\"data\":[{\"id\":\"deepseek/deepseek-chat\",\"context_length\":200000,"
      + "\"pricing\":{\"prompt\":\"0.000001\",\"completion\":\"0.000002\"},"
      + "\"supported_parameters\":[\"tools\"]}]}";

  [Fact]
  public async Task ACloudflarePull_CountsTheChatModels_AndPersistsThemUnderTheAccountKey()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    using RecordingHandler handler = new(CloudflareBody);
    ProviderCatalogProbe probe = new(db.Database, () => ClientFor(handler));

    Result<ProviderProbeResult> pulled = await probe.ProbeAsync(ModelVendors.Cloudflare,
        "acct-7", "token-7", null, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(pulled.IsSuccess, pulled.Error?.Message);
    // The embedding model is excluded before any entry is produced, so the count is chat models.
    Assert.Equal(1, pulled.Value.ModelCount);
    Assert.Contains("1 model is available", pulled.Value.Summary, StringComparison.Ordinal);
    Assert.Equal("https://api.cloudflare.com/client/v4/accounts/acct-7/ai/models/search",
        handler.Requests[0].RequestUri!.AbsoluteUri);
    Assert.Equal("Bearer token-7", handler.Requests[0].Headers.Authorization!.ToString());

    // The snapshot landed where the session's catalog looks for it.
    Result<ModelCatalogSnapshot> stored = await new SqliteModelCatalogCache(db.Database)
        .ReadAsync($"cloudflare|https://api.cloudflare.com/client/v4/accounts/acct-7/ai/models/search",
            TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(stored.IsSuccess, stored.Error?.Message);
    Assert.Contains(stored.Value.Entries, e => e.ModelId == "@cf/meta/llama-3.1-8b-instruct");
  }

  [Fact]
  public async Task AnOpenRouterPull_ReadsTheConfiguredHost_AndPersistsItsModels()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    using RecordingHandler handler = new(OpenRouterBody);
    ProviderCatalogProbe probe = new(db.Database, () => ClientFor(handler));
    Uri host = new("https://proxy.test/");

    Result<ProviderProbeResult> pulled = await probe.ProbeAsync(ModelVendors.OpenRouter,
        null, null, host, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(pulled.IsSuccess, pulled.Error?.Message);
    Assert.Equal(1, pulled.Value.ModelCount);
    Assert.Equal("https://proxy.test/api/v1/models", handler.Requests[0].RequestUri!.AbsoluteUri);
    Result<ModelCatalogSnapshot> stored = await new SqliteModelCatalogCache(db.Database)
        .ReadAsync("openrouter|https://proxy.test/api/v1/models",
            TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(stored.IsSuccess, stored.Error?.Message);
    Assert.Contains(stored.Value.Entries, e => e.ModelId == "deepseek/deepseek-chat");
  }

  [Fact]
  public async Task ACloudflarePull_WithoutBothHalves_IsRefusedWithoutAnyRequest()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    using RecordingHandler handler = new(CloudflareBody);
    ProviderCatalogProbe probe = new(db.Database, () => ClientFor(handler));

    Result<ProviderProbeResult> pulled = await probe.ProbeAsync(ModelVendors.Cloudflare,
        "acct-7", null, null, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(pulled.IsSuccess);
    Assert.Equal("ProviderNotConfigured", pulled.Error.Code);
    Assert.Empty(handler.Requests);
  }

  [Fact]
  public async Task ARefusedCredential_IsTheProvidersOwnNamedFailure()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    using RecordingHandler handler = new(/*lang=json,strict*/ "{\"errors\":[{\"message\":\"Invalid API Token\"}]}",
        HttpStatusCode.Forbidden);
    ProviderCatalogProbe probe = new(db.Database, () => ClientFor(handler));

    Result<ProviderProbeResult> pulled = await probe.ProbeAsync(ModelVendors.Cloudflare,
        "acct-7", "token-wrong", null, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(pulled.IsSuccess);
    Assert.Equal("ProviderUnauthorized", pulled.Error.Code);
    Assert.Contains("Invalid API Token", pulled.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AnUnknownVendor_IsRefusedByName()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    using RecordingHandler handler = new("{}");
    ProviderCatalogProbe probe = new(db.Database, () => ClientFor(handler));

    Result<ProviderProbeResult> pulled = await probe.ProbeAsync("gopher", null, null, null,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(pulled.IsSuccess);
    Assert.Equal("UnknownProvider", pulled.Error.Code);
    Assert.Contains("gopher", pulled.Error.Message, StringComparison.Ordinal);
  }

  /// <summary>The client a pull goes through, over the supplied handler. The catalog the pull
  ///     builds owns this client and disposes it when the pull ends, which is why the caller
  ///     does not.</summary>
  /// <param name="handler">The handler that answers the pull.</param>
  private static HttpClient ClientFor(HttpMessageHandler handler) => new(handler);

  /// <summary>A handler that answers every request with one body and records what it was
  ///     asked, so a test can assert the URL and credential a pull presented.</summary>
  private sealed class RecordingHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
      : HttpMessageHandler
  {
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
      Requests.Add(request);
      return Task.FromResult(new HttpResponseMessage(status)
      {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
      });
    }
  }
}
