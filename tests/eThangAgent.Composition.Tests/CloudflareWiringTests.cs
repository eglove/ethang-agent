using System.Text.Json;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Cloudflare.ACL;
using eThangAgent.ModelDomain;
using eThangAgent.OpenRouter.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>Both providers live in ONE container, each behind its vendor id, so a session whose
///     recorded binding names one vendor still serves a model the other provides. The recorded
///     binding is the session's identity; it no longer decides which provider exists.
///     <para>CLOUDFLARE IS ABSENT WITHOUT A CREDENTIAL - no transport, no catalog, and a
///     named refusal from the vendor-keyed resolver - which is what lets every surface hide
///     the provider instead of offering one that cannot answer.</para>
///     <para>Nothing here makes a network call: a loopback port nothing listens on stands in
///     for the provider hosts, and every assertion is about a registration, a resolved type,
///     or a built configuration.</para></summary>
[Trait("Category", "Process")]
public class CloudflareWiringTests
{
  /// <summary>A loopback port nothing listens on: a request to it fails at transport at once,
  ///     with no name resolution and no network.</summary>
  private static readonly Uri UnreachableBaseUrl = new("http://127.0.0.1:1");

  private static AgentSettings Settings(string? openRouterKey = "sk-or-test",
      string? cloudflareAccountId = "acct-1", string? cloudflareApiToken = "token-1") => new(
      new OpenRouterSettings(openRouterKey, UnreachableBaseUrl),
      new AgentRunOptions(null, 2),
      Cloudflare: cloudflareAccountId is null && cloudflareApiToken is null
          ? null
          : new CloudflareSettings(cloudflareAccountId, cloudflareApiToken));

  private static ServiceProvider Build(TestAppDatabase database, AgentSettings settings,
      string providerName) =>
      new ServiceCollection()
          .AddEThangAgentCore(settings, providerName,
              ModelConfig.Create("test/model", 512, 0.5f, 8192).Value!,
              new AgentHostOptions(
                  FixedWorkspaceContext.ForPath("app"), new UnrootedPathResolver()),
              database.Database)
          .BuildServiceProvider();

  /// <summary>The host reads its settings with the web defaults; the test reads the same
  ///     document the same way, so a member the host could not bind would fail here too.
  ///     </summary>
  private static JsonSerializerOptions HostJson { get; } = new(JsonSerializerDefaults.Web);

  private static ModelConfig Config(string modelId = "test/model") =>
      ModelConfig.Create(modelId, 512, 0.5f, 8192).Value!;

  [Fact]
  public void AnOpenRouterWiredContainer_ResolvesACloudflareProviderForACloudflareEntry()
  {
    using TestAppDatabase dbservices = TestAppDatabase.Create();
    using ServiceProvider services = Build(dbservices, Settings(), Providers.OpenRouter);

    Result<IModelProvider> resolved = services.GetRequiredService<IModelProviderResolver>()
        .Resolve(Providers.Cloudflare, Config("@cf/meta/llama-3.1-8b-instruct"));

    Assert.True(resolved.IsSuccess, resolved.Error?.Message);
    _ = Assert.IsType<CloudflareModelProvider>(resolved.Value);
  }

  [Fact]
  public void ACloudflareWiredContainer_ResolvesAnOpenRouterProviderForAnOpenRouterEntry()
  {
    using TestAppDatabase dbservices = TestAppDatabase.Create();
    using ServiceProvider services = Build(dbservices, Settings(), Providers.Cloudflare);

    Result<IModelProvider> resolved = services.GetRequiredService<IModelProviderResolver>()
        .Resolve(Providers.OpenRouter, Config("deepseek/deepseek-chat"));

    Assert.True(resolved.IsSuccess, resolved.Error?.Message);
    _ = Assert.IsType<OpenRouterModelProvider>(resolved.Value);
  }

  [Fact]
  public void BothCatalogs_AreRegistered_AndEachIsItsOwn()
  {
    using TestAppDatabase dbservices = TestAppDatabase.Create();
    using ServiceProvider services = Build(dbservices, Settings(), Providers.OpenRouter);
    IModelProviderResolver resolver = services.GetRequiredService<IModelProviderResolver>();

    Result<IModelCatalog> openRouter = resolver.Catalog(Providers.OpenRouter);
    Result<IModelCatalog> cloudflare = resolver.Catalog(Providers.Cloudflare);

    Assert.True(openRouter.IsSuccess, openRouter.Error?.Message);
    Assert.True(cloudflare.IsSuccess, cloudflare.Error?.Message);
    _ = Assert.IsType<OpenRouterCatalogClient>(openRouter.Value);
    _ = Assert.IsType<CloudflareModelCatalog>(cloudflare.Value);
    Assert.NotSame(openRouter.Value, cloudflare.Value);
  }

  [Fact]
  public void WithoutACloudflareCredential_TheVendorHasNoTransportAndNoCatalog()
  {
    using TestAppDatabase dbservices = TestAppDatabase.Create();
    using ServiceProvider services = Build(dbservices,
        Settings(cloudflareAccountId: null, cloudflareApiToken: null), Providers.OpenRouter);
    IModelProviderResolver resolver = services.GetRequiredService<IModelProviderResolver>();

    Result<IModelProvider> provider = resolver.Resolve(Providers.Cloudflare, Config());
    Result<IModelCatalog> catalog = resolver.Catalog(Providers.Cloudflare);

    Assert.False(provider.IsSuccess);
    Assert.Equal(ProviderResolver.ProviderNotConfigured, provider.Error.Code);
    Assert.Contains("Cloudflare", provider.Error.Message, StringComparison.Ordinal);
    Assert.False(catalog.IsSuccess);
    Assert.Equal(ProviderResolver.ProviderNotConfigured, catalog.Error.Code);
  }

  [Fact]
  public void WithoutACloudflareCredential_TheOpenRouterSurfaceStillWorks()
  {
    using TestAppDatabase dbservices = TestAppDatabase.Create();
    using ServiceProvider services = Build(dbservices,
        Settings(cloudflareAccountId: null, cloudflareApiToken: null), Providers.OpenRouter);
    IModelProviderResolver resolver = services.GetRequiredService<IModelProviderResolver>();

    Assert.True(resolver.Resolve(Providers.OpenRouter, Config()).IsSuccess);
    Assert.True(resolver.Catalog(Providers.OpenRouter).IsSuccess);
  }

  [Fact]
  public async Task AFailedCloudflareRead_LeavesTheOpenRouterCatalogResolvable()
  {
    // Each catalog is its own object behind its own key, so one vendor's failed read is that
    // vendor's failure and nothing else's: the other vendor's catalog is still resolvable and
    // still the same instance afterwards.
    using TestAppDatabase db = TestAppDatabase.Create();
    using ServiceProvider services = Build(db, Settings(), Providers.OpenRouter);
    IModelProviderResolver resolver = services.GetRequiredService<IModelProviderResolver>();
    CloudflareModelCatalog cloudflare = Assert.IsType<CloudflareModelCatalog>(
        resolver.Catalog(Providers.Cloudflare).Value, exactMatch: false);
    OpenRouterCatalogClient before = Assert.IsType<OpenRouterCatalogClient>(
        resolver.Catalog(Providers.OpenRouter).Value, exactMatch: false);

    Result<IReadOnlyList<ModelProviderEntry>> cloudflareRead =
        await cloudflare.GetAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    // The Cloudflare host is a loopback port nothing listens on, so the read is a NAMED
    // failure rather than an empty catalog or an exception.
    Assert.False(cloudflareRead.IsSuccess);
    OpenRouterCatalogClient after = Assert.IsType<OpenRouterCatalogClient>(
        resolver.Catalog(Providers.OpenRouter).Value, exactMatch: false);
    Assert.Same(before, after);
  }

  [Fact]
  public async Task ACloudflareSession_WithNoCredential_IsRefusedBeforeAnyHostIsLaunched()
  {
    // The refusal is a configuration fact reported before a container is built, which is why
    // this test starts no process: the credential check runs ahead of every transport.
    using TestAppDatabase db = TestAppDatabase.Create();
    AgentSettings settings = Settings(cloudflareAccountId: null, cloudflareApiToken: null);
    AgentSessionFactory factory = new(settings, db.Database);
    string workspace = TestTempRoot.CreateDirectory("ethang-cf-refused").FullName;

    Result<AgentSession> created = await factory.CreateAsync(
        workspace, Providers.Cloudflare, ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(created.IsSuccess);
    DomainError error = Assert.IsType<DomainError>(created.Error);
    Assert.Equal("ProviderNotConfigured", error.Code);
    Assert.Contains("Cloudflare", error.Message, StringComparison.Ordinal);
    Assert.Contains("account id and API token", error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void AnUnknownVendor_IsRefusedByName()
  {
    using TestAppDatabase dbservices = TestAppDatabase.Create();
    using ServiceProvider services = Build(dbservices, Settings(), Providers.OpenRouter);
    IModelProviderResolver resolver = services.GetRequiredService<IModelProviderResolver>();

    Result<IModelProvider> provider = resolver.Resolve("gopher", Config());
    Result<IModelCatalog> catalog = resolver.Catalog("gopher");

    DomainError providerError = Assert.IsType<DomainError>(provider.Error);
    DomainError catalogError = Assert.IsType<DomainError>(catalog.Error);

    Assert.Equal("UnknownProvider", providerError.Code);
    Assert.Contains("gopher", providerError.Message, StringComparison.Ordinal);
    Assert.Equal("UnknownProvider", catalogError.Code);
  }

  [Fact]
  public void TheUnkeyedResolves_AnswerForTheSessionsOwnVendor()
  {
    // Every vendor-unaware consumer (the root holder, the run loop, the compactor) resolves
    // these unkeyed, so the session's own provider must be what they get.
    using TestAppDatabase dbopenRouter = TestAppDatabase.Create();
    using ServiceProvider openRouter = Build(dbopenRouter, Settings(), Providers.OpenRouter);
    using TestAppDatabase dbcloudflare = TestAppDatabase.Create();
    using ServiceProvider cloudflare = Build(dbcloudflare, Settings(), Providers.Cloudflare);

    _ = Assert.IsType<OpenRouterModelProvider>(openRouter.GetRequiredService<IModelProvider>());
    _ = Assert.IsType<CloudflareModelProvider>(cloudflare.GetRequiredService<IModelProvider>());
    _ = Assert.IsType<OpenRouterCatalogClient>(openRouter.GetRequiredService<IModelCatalog>());
    _ = Assert.IsType<CloudflareModelCatalog>(cloudflare.GetRequiredService<IModelCatalog>());
  }

  [Fact]
  public void TheOpenRouterFactory_IsGivenTheOpenRouterCatalog_NotCloudflares()
  {
    using TestAppDatabase dbservices = TestAppDatabase.Create();
    using ServiceProvider services = Build(dbservices, Settings(), Providers.OpenRouter);
    IModelProviderFactory factory =
        services.GetRequiredKeyedService<IModelProviderFactory>(Providers.OpenRouter);

    IModelProvider provider = factory.Create(Config("deepseek/deepseek-chat"));

    _ = Assert.IsType<OpenRouterModelProvider>(provider);
  }

  [Fact]
  public void TheCloudflareTransport_IsConfiguredFromTheCredentialAndTheVersionedApiRoot()
  {
    using TestAppDatabase dbservices = TestAppDatabase.Create();
    using ServiceProvider services = Build(dbservices, Settings(cloudflareAccountId: "acct-9"), Providers.OpenRouter);

    CloudflareConfiguration configuration = services.GetRequiredService<CloudflareConfiguration>();

    Assert.Equal("acct-9", configuration.AccountId);
    Assert.Equal("token-1", configuration.ApiToken);
    Assert.Equal(
        "https://api.cloudflare.com/client/v4/accounts/acct-9/ai/models/search",
        configuration.Endpoint("ai/models/search").AbsoluteUri);
  }

  [Fact]
  public void ACloudflareSession_Builds_AndDoesNotThrowFromTheFallbackModelSwitch()
  {
    using TestAppDatabase dbservices = TestAppDatabase.Create();
    using ServiceProvider services = Build(dbservices, Settings(), Providers.Cloudflare);

    // The bootstrap model and the root resolver's fallback both come from
    // Providers.FallbackModelId, which used to throw for any id but OpenRouter.
    Assert.NotNull(services.GetRequiredService<RootAgentHolder>());
    Assert.NotNull(services.GetRequiredService<RootAgentResolver>());
    Assert.Equal(Providers.CloudflareDefaultModelId, Providers.FallbackModelId(Providers.Cloudflare));
  }

  [Fact]
  public void ACloudflareSession_WithoutACredential_RefusesWithANamedError()
  {
    AgentSettings settings = Settings(cloudflareAccountId: null, cloudflareApiToken: null);

    using TestAppDatabase db = TestAppDatabase.Create();
    Exception? ex = Record.Exception(() => Build(db, settings, Providers.Cloudflare));

    // A configuration fact, not an ArgumentOutOfRangeException from a provider switch.
    InvalidOperationException invalid = Assert.IsType<InvalidOperationException>(ex);
    Assert.Contains("Cloudflare", invalid.Message, StringComparison.Ordinal);
    Assert.Contains("API token", invalid.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void TheRecordedProviderName_NoLongerDecidesWhichProviderIsWired()
  {
    using TestAppDatabase dbservices = TestAppDatabase.Create();
    using ServiceProvider services = Build(dbservices, Settings(), Providers.Cloudflare);
    IModelProviderResolver resolver = services.GetRequiredService<IModelProviderResolver>();

    // The container was built for Cloudflare and still serves an OpenRouter entry.
    Assert.True(resolver.Resolve(Providers.OpenRouter, Config("deepseek/deepseek-chat")).IsSuccess);
    Assert.True(resolver.Catalog(Providers.OpenRouter).IsSuccess);
  }

  [Fact]
  public async Task TheCredential_TravelsToAChildHostInsideTheSettingsJson()
  {
    string scratch = TestTempRoot.CreateDirectory("ethang-cf-ship").FullName;
    try
    {
      string root = TestTempRoot.CreateDirectory("ethang-cf-ship-root").FullName;
      AgentSettings settings = Settings().WithWorkspaceRoot(root);
      RemoteHostSupervisor supervisor = new("conv-cf", scratch, settings,
          Path.Combine(scratch, "app.db"), _ => { });
      try
      {
        string json = await File.ReadAllTextAsync(
            Path.Combine(scratch, "childhost-settings.json"),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        AgentSettings shipped =
            JsonSerializer.Deserialize<AgentSettings>(json, HostJson)!;

        Assert.Equal("acct-1", shipped.Cloudflare!.AccountId);
        Assert.Equal("token-1", shipped.Cloudflare.ApiToken);
        Assert.True(shipped.HasCloudflare);
      }
      finally
      {
        await supervisor.DisposeAsync().ConfigureAwait(true);
      }
    }
    finally
    {
      Directory.Delete(scratch, recursive: true);
    }
  }

  [Fact]
  public void TheOpenRouterCredentialAndBaseUrl_AreUnchanged()
  {
    using TestAppDatabase dbservices = TestAppDatabase.Create();
    using ServiceProvider services = Build(dbservices, Settings(openRouterKey: "sk-or-kept"), Providers.OpenRouter);

    OpenRouterConfiguration configuration = services.GetRequiredService<OpenRouterConfiguration>();

    Assert.Equal("sk-or-kept", configuration.ApiKey);
    Assert.Equal(UnreachableBaseUrl, configuration.BaseUrl);
    Assert.Equal("openrouter_api_key", OpenRouterSettings.PreferenceKey);
    Assert.Equal("openrouter_management_key", OpenRouterSettings.ManagementKeyPreferenceKey);
  }
}
