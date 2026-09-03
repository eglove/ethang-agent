using System.Reflection;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Local.ACL;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

// Best-effort temp-file cleanup in finally blocks is deliberate (CA1031).
#pragma warning disable CA1031, S108 // Do not catch general exception types

namespace eThangAgent.Composition.Tests;

/// <summary>The local provider's composition arm: an OpenAI-compatible local server
///     (llama.cpp, LM Studio, Ollama) wired exactly like the cloud providers — its own
///     configuration, named HttpClient, typed provider client, factory, and catalog —
///     with no automatic selector (like z.ai) and the session's resolved bootstrap
///     model id threaded everywhere the static provider default fallback used to be.
///     Strictness pins: selecting local without a usable base URL aborts loudly, and
///     selecting it with no configuration fails ProviderNotConfigured before any
///     HTTP infrastructure is built.</summary>
public class LocalCompositionTests
{
  private sealed class SilentClarifyChannel : IClarifyChannel
  {
    public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default)
        => throw new NotSupportedException("No test should reach the human.");
  }

  private static AgentSettings Settings(LocalSettings? local) => new(
      new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
      new ZaiSettings(null, new Uri("https://zai.test")),
      new SubAgentOptions(null, 2),
      Local: local);

  private static LocalSettings LocalAt(string? baseUrlText = "http://localhost:5001") =>
      new(baseUrlText, ApiKey: "llama-key");

  private static ServiceProvider BuildCore(LocalSettings? local, string? resolvedFallbackModelId) =>
      new ServiceCollection()
          .AddEThangAgentCore(Settings(local), Providers.Local,
              ModelConfig.Create("local/bootstrap", null, 512, 0.5f, 8192).Value!,
              new AgentHostOptions(new SilentClarifyChannel(),
                  new FixedWorkspaceContext("app"), new UnrootedPathResolver()),
              resolvedFallbackModelId: resolvedFallbackModelId)
          .BuildServiceProvider();

  private static (AgentSessionFactory Factory, string DbPath) CreateFactory(AgentSettings settings)
  {
    string dbPath = Path.Combine(Path.GetTempPath(), $"ethang-local-comp-{Guid.NewGuid():N}.db");
    Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", dbPath);
    return (new AgentSessionFactory(settings), dbPath);
  }

  [Fact]
  public void Local_Configured_Resolves_LocalProvider_Factory_AndCatalog_AndNoSelector()
  {
    using ServiceProvider services = BuildCore(LocalAt(), resolvedFallbackModelId: "test-model");
    _ = Assert.IsType<LocalModelProvider>(services.GetRequiredService<IModelProvider>());
    _ = Assert.IsType<LocalModelProviderFactory>(services.GetRequiredService<IModelProviderFactory>());
    _ = Assert.IsType<LocalModelCatalog>(services.GetRequiredService<IModelCatalog>());
    // Non-OpenRouter providers run no automatic selection: the fallback (or the
    // user's picker choice) serves every turn.
    Assert.Null(services.GetService<IModelSelector>());
  }

  [Fact]
  public void Local_Configuration_Carries_Resolved_BaseUrl_And_ApiKey()
  {
    using ServiceProvider services = BuildCore(LocalAt(), resolvedFallbackModelId: "test-model");
    LocalConfiguration config = services.GetRequiredService<LocalConfiguration>();
    Assert.Equal(new Uri("http://localhost:5001"), config.BaseUrl);
    Assert.Equal("llama-key", config.ApiKey);
  }

  [Fact]
  public void Local_NamedHttpClient_Matches_The_Cloud_Provider_Arms()
  {
    using ServiceProvider services = BuildCore(LocalAt(), resolvedFallbackModelId: "test-model");
    using HttpClient named = services.GetRequiredService<IHttpClientFactory>().CreateClient("Local");
    Assert.Equal(TimeSpan.FromSeconds(120), named.Timeout);
  }

  [Fact]
  public void Local_Fallback_Ids_Are_Threaded_From_The_Resolved_Bootstrap_Model()
  {
    // The bootstrap model travels as the FALLBACK: children's SpawnOptions and both
    // root resolvers must serve the resolved id (Task 9 supplies the local server's
    // real bootstrap model), never the static Providers.FallbackModelId default.
    using ServiceProvider services = BuildCore(LocalAt(), resolvedFallbackModelId: "test-model");
    const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    // SpawnOptions is constructed inline in the StartSpawnHandler registration (not a
    // registered service), so the pin reads the handler's _spawn field.
    Assert.Equal("test-model", ((SpawnOptions)typeof(StartSpawnHandler)
        .GetField("_spawn", PrivateInstance)!
        .GetValue(services.GetRequiredService<IAgentSpawnCommand>())!).FallbackModelId);
    Assert.Equal("test-model", typeof(RootAgentResolver)
        .GetField("_fallbackModelId", PrivateInstance)!
        .GetValue(services.GetRequiredService<RootAgentResolver>()));
    Assert.Equal("test-model", typeof(ProviderFailoverResolver)
        .GetField("_fallbackModelId", PrivateInstance)!
        .GetValue(services.GetRequiredService<ProviderFailoverResolver>()));
  }

  [Fact]
  public void Local_Configured_WithoutApiKey_Resolves_TheProvider()
  {
    // Most local servers need no key; null must compose, never trip the
    // missing-API-key strictness that governs the cloud providers.
    using ServiceProvider services = BuildCore(new LocalSettings("http://localhost:5001", null),
        resolvedFallbackModelId: "test-model");
    _ = Assert.IsType<LocalModelProvider>(services.GetRequiredService<IModelProvider>());
    Assert.Null(services.GetRequiredService<LocalConfiguration>().ApiKey);
  }

  [Fact]
  public void Local_Selected_WithNoLocalSettings_Throws_InvalidOperationException_NotNullReference()
  {
    // The core-composition strictness: selecting local with NO local settings at
    // all aborts with the provider-not-configured InvalidOperationException — a
    // NullReferenceException would leak the wiring bug instead of naming the fix.
    Exception? ex = Record.Exception(() =>
        BuildCore(local: null, resolvedFallbackModelId: "test-model"));
    _ = Assert.IsType<InvalidOperationException>(ex);
  }

  [Fact]
  public void Local_Selected_WithBlankBaseUrl_Throws_InvalidOperationException()
  {
    // Same strictness as the missing API key: a selected provider whose base URL
    // cannot resolve aborts composition, and the message names the fix.
    Exception? ex = Record.Exception(() =>
        BuildCore(LocalAt(baseUrlText: "   "), resolvedFallbackModelId: "test-model"));
    InvalidOperationException invalid = Assert.IsType<InvalidOperationException>(ex);
    Assert.Contains("base URL", invalid.Message, StringComparison.Ordinal);
    Assert.Contains("Settings", invalid.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Local_Selected_WithUnparsableBaseUrl_Throws_InvalidOperationException()
  {
    Exception? ex = Record.Exception(() =>
        BuildCore(LocalAt(baseUrlText: "not-a-url"), resolvedFallbackModelId: "test-model"));
    _ = Assert.IsType<InvalidOperationException>(ex);
  }

  [Fact]
  public async Task Local_Selected_WithoutConfiguration_Fails_ProviderNotConfigured_BeforeAnyHttpCall()
  {
    // Local == null keeps HasLocal false: the structured failure fires in
    // ValidateProvider, which runs BEFORE any container — and therefore any
    // HttpClient — is ever built. Nothing can reach the network.
    (AgentSessionFactory factory, string db) = CreateFactory(Settings(local: null));
    try
    {
      DirectoryInfo dir = Directory.CreateTempSubdirectory("ethang-ws-local-unset");
      try
      {
        Result<AgentSession> result = await factory.CreateAsync(
            dir.FullName, Providers.Local, new SilentClarifyChannel(),
            ct: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("ProviderNotConfigured", result.Error.Code);
        Assert.Contains("Local (OpenAI-compatible)", result.Error.Message, StringComparison.Ordinal);
      }
      finally
      {
        dir.Delete(true);
      }
    }
    finally
    {
      Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", null);
      try
      {
        File.Delete(db);
      }
      catch
      {
      }
    }
  }

  [Fact]
  public void Unknown_Provider_Name_At_Composition_Throws_Listing_All_Three_Provider_Ids()
  {
    Exception? ex = Record.Exception(() => new ServiceCollection()
        .AddEThangAgentCore(Settings(local: null), "anthropic",
            ModelConfig.Create("m", null, 512, 0.5f, 8192).Value!,
            new AgentHostOptions(new SilentClarifyChannel(),
                new FixedWorkspaceContext("app"), new UnrootedPathResolver())));

    ArgumentException argument = Assert.IsType<ArgumentException>(ex);
    Assert.All(
        [Providers.OpenRouter, Providers.Zai, Providers.Local],
        id => Assert.Contains(id, argument.Message, StringComparison.Ordinal));
  }

  [Fact]
  public async Task Unknown_Provider_On_Session_Open_Fails_Listing_All_Three_Provider_Ids()
  {
    (AgentSessionFactory factory, string db) = CreateFactory(Settings(local: null));
    try
    {
      DirectoryInfo dir = Directory.CreateTempSubdirectory("ethang-ws-local-unknown");
      try
      {
        Result<AgentSession> result = await factory.CreateAsync(
            dir.FullName, "anthropic", new SilentClarifyChannel(),
            ct: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("UnknownProvider", result.Error.Code);
        Assert.All(
            [Providers.OpenRouter, Providers.Zai, Providers.Local],
            id => Assert.Contains(id, result.Error.Message, StringComparison.Ordinal));
      }
      finally
      {
        dir.Delete(true);
      }
    }
    finally
    {
      Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", null);
      try
      {
        File.Delete(db);
      }
      catch
      {
      }
    }
  }
}
