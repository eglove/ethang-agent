using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.MemoryDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;
using eThangAgent.StateDomain;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using eThangAgent.Zai.ACL;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

public class CompositionGuardTests
{
  private static AgentSettings Settings(string? openRouterKey = "sk-or-test", string? zaiKey = null) => new(
      new OpenRouterSettings(openRouterKey, new Uri("https://openrouter.test")),
      new ZaiSettings(zaiKey, new Uri("https://zai.test")),
      new SubAgentOptions(null, TimeSpan.FromSeconds(300), 2));

  [Fact]
  public void SubAgentDefaultModel_FallsBackToRootModel_WhenConfigOmitsIt()
  {
    AgentSettings settings = Settings();
    using ServiceProvider services = new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("root/model", null, 512, 0.5f).Value!,
            new AgentHostOptions(new StubClarifyChannel(),
                new FixedWorkspaceContext("app"), new UnrootedPathResolver()))
        .BuildServiceProvider();

    SubAgentOptions options = services.GetRequiredService<SubAgentOptions>();
    Assert.Equal("root/model", options.DefaultModel);
    Assert.Equal(TimeSpan.FromSeconds(300), options.ChildTimeout); // preserved
    Assert.Equal(2, options.MaxConcurrentAgents);                  // preserved
  }

  public static TheoryData<string, AgentHostOptions> BothHostShapes => new()
    {
        { "terminal-shaped", new AgentHostOptions(
            new StubClarifyChannel(),
            new FixedWorkspaceContext(Path.GetFullPath(".")),
            new WorkspacePathResolver(Path.GetFullPath("."))) },
        { "desktop-shaped", new AgentHostOptions(
            new StubClarifyChannel(),
            new FixedWorkspaceContext("app"),
            new UnrootedPathResolver()) },
    };

  [Theory]
  [MemberData(nameof(BothHostShapes), DisableDiscoveryEnumeration = true)]
  public void Core_Graph_Resolves_Every_Service_For_Every_Host(string label, AgentHostOptions host)
  {
    Assert.False(string.IsNullOrWhiteSpace(label));
    AgentSettings settings = Settings();
    using ServiceProvider services = new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", null, 512, 0.5f).Value!, host)
        .BuildServiceProvider();

    object?[] resolutions =
    [
        services.GetRequiredService<RootAgentHolder>(),
            services.GetRequiredService<RootAgentResolver>(),
            services.GetRequiredService<SendMessageCommandHandler>(),
            services.GetRequiredService<Conversation>(),
            services.GetRequiredService<IConversationRepository>(),
            services.GetRequiredService<IFileSystemAccess>(),
            services.GetRequiredService<IFileWriteAccess>(),
            services.GetRequiredService<IFileEditAccess>(),
            services.GetRequiredService<ISearchAccess>(),
            services.GetRequiredService<IGitQueryAccess>(),
            services.GetRequiredService<IGitCommitAccess>(),
            services.GetRequiredService<IExecEngine>(),
            services.GetRequiredService<IToolRegistry>(),
            services.GetRequiredService<ITool>(),
            services.GetRequiredService<ICapabilityRegistry>(),
            services.GetRequiredService<IStateService>(),
            services.GetRequiredService<IStateStore>(),
            services.GetRequiredService<IAgentStore>(),
            services.GetRequiredService<AppDatabase>(),
            services.GetRequiredService<ISkillCatalog>(),
            services.GetRequiredService<ILearnedSkillStore>(),
            services.GetRequiredService<ICuratedMemoryStore>(),
            services.GetRequiredService<IClarifyChannel>(),
            services.GetRequiredService<IWorkspaceContext>(),
            services.GetRequiredService<IPathResolver>(),
            services.GetRequiredService<IModelProvider>(),
            services.GetRequiredService<IModelProviderFactory>(),
            services.GetRequiredService<IAgentRuntime>(),
            services.GetRequiredService<IAgentSpawnCommand>(),
            services.GetRequiredService<IMemoryRecallQuery>(),
            services.GetRequiredService<ISystemPromptProvider>(),
            services.GetRequiredService<SubAgentSpawner>(),
            services.GetRequiredService<RootSessionLifecycle>(),
            services.GetRequiredService<ModelConfig>(),
        ];
    Assert.All(resolutions, Assert.NotNull);
  }

  [Fact]
  public void ZaiWiring_Resolves_ZaiProvider_Factory_AndCatalog()
  {
    AgentSettings settings = Settings(zaiKey: "zai-test-key");
    using ServiceProvider services = new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.Zai,
            ModelConfig.Create("glm-5.3", null, 512, 0.5f).Value!,
            new AgentHostOptions(new StubClarifyChannel(),
                new FixedWorkspaceContext("app"), new UnrootedPathResolver()))
        .BuildServiceProvider();

    _ = Assert.IsType<ZaiModelProvider>(services.GetRequiredService<IModelProvider>());
    _ = Assert.IsType<ZaiModelProviderFactory>(services.GetRequiredService<IModelProviderFactory>());
    _ = Assert.IsType<ZaiModelCatalog>(services.GetRequiredService<IModelCatalog>());
  }

  [Fact]
  public void SelectedProvider_WithoutApiKey_Throws()
  {
    AgentSettings settings = Settings(openRouterKey: null);
    Exception ex = Record.Exception(() => new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("m", null, 512, 0.5f).Value!,
            new AgentHostOptions(new StubClarifyChannel(),
                new FixedWorkspaceContext("app"), new UnrootedPathResolver())));

    InvalidOperationException invalid = Assert.IsType<InvalidOperationException>(ex);
    Assert.Contains("API key", invalid.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void UnknownProviderName_Throws_ArgumentException()
  {
    AgentSettings settings = Settings();
    Exception ex = Record.Exception(() => new ServiceCollection()
        .AddEThangAgentCore(settings, "anthropic",
            ModelConfig.Create("m", null, 512, 0.5f).Value!,
            new AgentHostOptions(new StubClarifyChannel(),
                new FixedWorkspaceContext("app"), new UnrootedPathResolver())));

    ArgumentException argument = Assert.IsType<ArgumentException>(ex);
    Assert.Contains("anthropic", argument.Message, StringComparison.Ordinal);
  }

  private sealed class StubClarifyChannel : IClarifyChannel
  {
    public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default)
        => Task.FromResult(Result.Success("1"));
  }
}
