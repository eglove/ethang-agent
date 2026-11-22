using System.Net;
using System.Text;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Cloudflare.ACL;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>The defect this pins: a Cloudflare credential entered while the app was running had no
///     effect until the app was restarted, because the session's container had captured the
///     credential it was built with and its catalog had already cached an empty answer.
///     <para>ONE ACTION FIXES BOTH HALVES. The Test button pulls the catalog with the credential
///     in the form; the shell then pushes that credential onto the open session's carrier and
///     hands the pulled models to the session's own catalog, so the model search lists them
///     without anything being reopened.</para></summary>
[Trait("Category", "Process")]
public class ProviderTestFillsModelSearchTests
{
  private const string CatalogBody =
      /*lang=json,strict*/ "{\"result\":[{\"name\":\"@cf/meta/llama-3.1-8b-instruct\","
      + "\"task\":{\"name\":\"Text Generation\"}}]}";

  [Fact]
  public async Task ATestedCredential_ReachesTheOpenSession_AndItsModelSearch()
  {
    using TestAppDatabase database = TestAppDatabase.Create();
    // The session was opened BEFORE any Cloudflare credential existed, which is the state the
    // user was in: the app was running with no Cloudflare key.
    AgentSettings settings = new(new OpenRouterSettings("sk-or-test", new Uri("http://127.0.0.1:1")),
        new AgentRunOptions(null, 1));
    using ServiceProvider services = new ServiceCollection().AddEThangAgentCore(settings,
        Providers.OpenRouter, ModelConfig.Create("openrouter/auto", 256, 0.2f, 200_000).Value!,
        new AgentHostOptions(FixedWorkspaceContext.ForPath("test"), new UnrootedPathResolver()),
        database.Database).BuildServiceProvider();
    AgentSession session = new(services, AgentId.NewId(), services.GetRequiredService<Conversation>(),
        services.GetRequiredService<SendMessageCommandHandler>(),
        services.GetRequiredService<RootSessionLifecycle>(),
        services.GetRequiredService<ModelConfig>(), Directory.GetCurrentDirectory(),
        Providers.OpenRouter, services.GetRequiredService<IAgentInbox>(),
        services.GetRequiredService<IAgentRuntime>());
    using RecordingHandler handler = new(CatalogBody);
    ProviderCatalogProbe probe = new(database.Database, () => new HttpClient(handler));
    MainViewModel shell = new((_, _) => Task.FromResult(Result.Success(session)),
        new MainViewModelOptions
        {
          AvailableProviders = [new ProviderOption(Providers.OpenRouter, "OpenRouter")],
          PreferredProviderId = Providers.OpenRouter,
          ProviderProbe = probe.ProbeAsync,
        });
    Result<AgentTabViewModel> opened = await shell.OpenAgentAsync(
        session.WorkspaceRoot, Providers.OpenRouter);
    Assert.True(opened.IsSuccess, opened.Error?.Message);
    CloudflareCredentialCarrier carrier = services.GetRequiredService<CloudflareCredentialCarrier>();
    Assert.False(carrier.IsConfigured);

    Result<ProviderProbeResult> tested = await shell.ProbeProviderAsync(ModelVendors.Cloudflare,
        "acct-late", "token-late", null, TestContext.Current.CancellationToken);

    Assert.True(tested.IsSuccess, tested.Error?.Message);
    // The credential is live on the session that was already open...
    Assert.True(carrier.IsConfigured);
    Assert.Equal("acct-late", carrier.AccountId);
    // ...and the session's own catalog serves the pulled models, with no second HTTP call.
    Result<IModelCatalog> resolved = services.GetRequiredService<IModelProviderResolver>()
        .Catalog(ModelVendors.Cloudflare);
    Assert.True(resolved.IsSuccess, resolved.Error?.Message);
    Result<IReadOnlyList<ModelProviderEntry>> served = await resolved.Value
        .GetAsync(TestContext.Current.CancellationToken);
    Assert.True(served.IsSuccess, served.Error?.Message);
    Assert.Contains(served.Value, e => e.ModelId == "@cf/meta/llama-3.1-8b-instruct");
    _ = Assert.Single(handler.Requests);
  }

  [Fact]
  public async Task AFailedTest_LeavesTheOpenSessionUntouched()
  {
    using TestAppDatabase database = TestAppDatabase.Create();
    AgentSettings settings = new(new OpenRouterSettings("sk-or-test", new Uri("http://127.0.0.1:1")),
        new AgentRunOptions(null, 1),
        Cloudflare: new CloudflareSettings("acct-good", "token-good"));
    using ServiceProvider services = new ServiceCollection().AddEThangAgentCore(settings,
        Providers.OpenRouter, ModelConfig.Create("openrouter/auto", 256, 0.2f, 200_000).Value!,
        new AgentHostOptions(FixedWorkspaceContext.ForPath("test"), new UnrootedPathResolver()),
        database.Database).BuildServiceProvider();
    AgentSession session = new(services, AgentId.NewId(), services.GetRequiredService<Conversation>(),
        services.GetRequiredService<SendMessageCommandHandler>(),
        services.GetRequiredService<RootSessionLifecycle>(),
        services.GetRequiredService<ModelConfig>(), Directory.GetCurrentDirectory(),
        Providers.OpenRouter, services.GetRequiredService<IAgentInbox>(),
        services.GetRequiredService<IAgentRuntime>());
    using RecordingHandler handler = new("{}", HttpStatusCode.Forbidden);
    ProviderCatalogProbe probe = new(database.Database, () => new HttpClient(handler));
    MainViewModel shell = new((_, _) => Task.FromResult(Result.Success(session)),
        new MainViewModelOptions
        {
          AvailableProviders = [new ProviderOption(Providers.OpenRouter, "OpenRouter")],
          PreferredProviderId = Providers.OpenRouter,
          ProviderProbe = probe.ProbeAsync,
        });
    Result<AgentTabViewModel> opened = await shell.OpenAgentAsync(
        session.WorkspaceRoot, Providers.OpenRouter);
    Assert.True(opened.IsSuccess, opened.Error?.Message);
    CloudflareCredentialCarrier carrier = services.GetRequiredService<CloudflareCredentialCarrier>();

    Result<ProviderProbeResult> tested = await shell.ProbeProviderAsync(ModelVendors.Cloudflare,
        "acct-typo", "token-wrong", null, TestContext.Current.CancellationToken);

    Assert.False(tested.IsSuccess);
    // A wrong key must not break the session that was working: the carrier still holds the
    // credential the session was built with.
    Assert.Equal("acct-good", carrier.AccountId);
    Assert.Equal("token-good", carrier.ApiToken);
  }

  [Fact]
  public async Task ASavedCredential_ReachesTheOpenSession_WithNoRestart()
  {
    // The reported defect: the key was entered, the app was running, and the models only
    // appeared after a restart. Saving now pushes the credential onto the session that is
    // already open, which is what makes the next call use it.
    using TestAppDatabase database = TestAppDatabase.Create();
    AgentSettings settings = new(new OpenRouterSettings("sk-or-test", new Uri("http://127.0.0.1:1")),
        new AgentRunOptions(null, 1));
    using ServiceProvider services = new ServiceCollection().AddEThangAgentCore(settings,
        Providers.OpenRouter, ModelConfig.Create("openrouter/auto", 256, 0.2f, 200_000).Value!,
        new AgentHostOptions(FixedWorkspaceContext.ForPath("test"), new UnrootedPathResolver()),
        database.Database).BuildServiceProvider();
    AgentSession session = new(services, AgentId.NewId(), services.GetRequiredService<Conversation>(),
        services.GetRequiredService<SendMessageCommandHandler>(),
        services.GetRequiredService<RootSessionLifecycle>(),
        services.GetRequiredService<ModelConfig>(), Directory.GetCurrentDirectory(),
        Providers.OpenRouter, services.GetRequiredService<IAgentInbox>(),
        services.GetRequiredService<IAgentRuntime>());
    MainViewModel shell = new((_, _) => Task.FromResult(Result.Success(session)),
        new MainViewModelOptions
        {
          AvailableProviders = [new ProviderOption(Providers.OpenRouter, "OpenRouter")],
          PreferredProviderId = Providers.OpenRouter,
          Settings = settings,
          SessionFactory = new AgentSessionFactory(settings, database.Database),
          Preferences = new SqliteAppPreferenceStore(database.Database),
        });
    Result<AgentTabViewModel> opened = await shell.OpenAgentAsync(
        session.WorkspaceRoot, Providers.OpenRouter);
    Assert.True(opened.IsSuccess, opened.Error?.Message);
    CloudflareCredentialCarrier carrier = services.GetRequiredService<CloudflareCredentialCarrier>();
    Assert.False(carrier.IsConfigured);

    await shell.ApplySettingsAsync(new SettingsUpdate("sk-or-test", CommitStyle.Conventional,
        CloudflareAccountId: "acct-saved", CloudflareApiToken: "token-saved",
        CloudflareGatewayId: "gw-saved"));

    Assert.True(carrier.IsConfigured);
    Assert.Equal("acct-saved", carrier.AccountId);
    Assert.Equal("token-saved", carrier.ApiToken);
    // The gateway is part of the same configuration, so it is live on the open session too:
    // the next call names it without the session being reopened.
    Assert.Equal("gw-saved", carrier.GatewayId);
    Assert.Equal("gw-saved",
        services.GetRequiredService<CloudflareConfiguration>().EffectiveGatewayId);
  }

  [Fact]
  public async Task AClearedGatewayId_StopsBeingNamedByTheOpenSession()
  {
    using TestAppDatabase database = TestAppDatabase.Create();
    AgentSettings settings = new(new OpenRouterSettings("sk-or-test", new Uri("http://127.0.0.1:1")),
        new AgentRunOptions(null, 1),
        Cloudflare: new CloudflareSettings("acct-good", "token-good", "gw-old"));
    using ServiceProvider services = new ServiceCollection().AddEThangAgentCore(settings,
        Providers.OpenRouter, ModelConfig.Create("openrouter/auto", 256, 0.2f, 200_000).Value!,
        new AgentHostOptions(FixedWorkspaceContext.ForPath("test"), new UnrootedPathResolver()),
        database.Database).BuildServiceProvider();
    AgentSession session = new(services, AgentId.NewId(), services.GetRequiredService<Conversation>(),
        services.GetRequiredService<SendMessageCommandHandler>(),
        services.GetRequiredService<RootSessionLifecycle>(),
        services.GetRequiredService<ModelConfig>(), Directory.GetCurrentDirectory(),
        Providers.OpenRouter, services.GetRequiredService<IAgentInbox>(),
        services.GetRequiredService<IAgentRuntime>());
    MainViewModel shell = new((_, _) => Task.FromResult(Result.Success(session)),
        new MainViewModelOptions
        {
          AvailableProviders = [new ProviderOption(Providers.OpenRouter, "OpenRouter")],
          PreferredProviderId = Providers.OpenRouter,
          Settings = settings,
          SessionFactory = new AgentSessionFactory(settings, database.Database),
          Preferences = new SqliteAppPreferenceStore(database.Database),
        });
    Result<AgentTabViewModel> opened = await shell.OpenAgentAsync(
        session.WorkspaceRoot, Providers.OpenRouter);
    Assert.True(opened.IsSuccess, opened.Error?.Message);
    CloudflareCredentialCarrier carrier = services.GetRequiredService<CloudflareCredentialCarrier>();
    Assert.Equal("gw-old", carrier.GatewayId);

    await shell.ApplySettingsAsync(new SettingsUpdate("sk-or-test", CommitStyle.Conventional,
        CloudflareAccountId: "acct-good", CloudflareApiToken: "token-good",
        CloudflareGatewayId: null));

    // A save that states no gateway clears it rather than resurrecting the value it replaced.
    Assert.Null(carrier.GatewayId);
    Assert.Equal("acct-good", carrier.AccountId);
  }

  /// <summary>A handler that answers every request with one body and records what it was asked.
  ///     </summary>
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
