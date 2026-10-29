using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SkillDomain;
using eThangAgent.StateDomain;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

public class CompositionGuardTests
{
  private static AgentSettings Settings(string? openRouterKey = "sk-or-test") => new(
      new OpenRouterSettings(openRouterKey, new Uri("https://openrouter.test")),
      new SubAgentOptions(null, 2));

  [Fact]
  public void SubAgentDefaultModel_FallsBackToRootModel_WhenConfigOmitsIt()
  {
    AgentSettings settings = Settings();
    using TestAppDatabase db = TestAppDatabase.Create();
    using ServiceProvider services = new ServiceCollection()
    .AddEThangAgentCore(settings, Providers.OpenRouter,
        ModelConfig.Create("root/model", 512, 0.5f, 8192).Value!,
        new AgentHostOptions(
            FixedWorkspaceContext.ForPath("app"), new UnrootedPathResolver()), db.Database)
    .BuildServiceProvider();

    SubAgentOptions options = services.GetRequiredService<SubAgentOptions>();
    Assert.Equal("root/model", options.DefaultModel);
    Assert.Equal(2, options.MaxConcurrentAgents); // preserved
  }

  public static TheoryData<string, AgentHostOptions> BothHostShapes => new()
    {
        { "terminal-shaped", new AgentHostOptions(
            FixedWorkspaceContext.ForPath(Path.GetFullPath(".")),
            new WorkspacePathResolver(Path.GetFullPath("."))) },
        { "desktop-shaped", new AgentHostOptions(
            FixedWorkspaceContext.ForPath("app"),
            new UnrootedPathResolver()) },
    };

  [Theory]
  [MemberData(nameof(BothHostShapes), DisableDiscoveryEnumeration = true)]
  public void Core_Graph_Resolves_Every_Service_For_Every_Host(string label, AgentHostOptions host)
  {
    Assert.False(string.IsNullOrWhiteSpace(label));
    AgentSettings settings = Settings();
    using TestAppDatabase db = TestAppDatabase.Create();
    using ServiceProvider services = new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", 512, 0.5f, 8192).Value!, host, db.Database)
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
            services.GetRequiredService<ILineEndingPolicy>(),
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
  public void Worktree_Tool_Is_In_The_Session_Surface()
  {
    // The worktree capability is core wiring, not provider-specific: every
    // session's tool surface carries it.
    using TestAppDatabase db = TestAppDatabase.Create();
    using ServiceProvider services = new ServiceCollection()
    .AddEThangAgentCore(Settings(), Providers.OpenRouter,
        ModelConfig.Create("m", 512, 0.5f, 8192).Value!,
        new AgentHostOptions(
            FixedWorkspaceContext.ForPath("app"), new UnrootedPathResolver()), db.Database)
    .BuildServiceProvider();

    AgentToolsProvider tools = services.GetRequiredService<AgentToolsProvider>();
    Assert.Contains(tools.Actions, a => a.Name == "worktree");
  }

  [Fact]
  public void OpenRouterSession_ResolvesTheRootResolver()
  {
    // The resolver wiring resolves lazily: a container build must not throw.
    using TestAppDatabase db = TestAppDatabase.Create();
    using ServiceProvider openRouterServices = new ServiceCollection()
    .AddEThangAgentCore(Settings(), Providers.OpenRouter,
        ModelConfig.Create("m", 512, 0.5f, 8192).Value!,
        new AgentHostOptions(
            FixedWorkspaceContext.ForPath("app"), new UnrootedPathResolver()), db.Database)
    .BuildServiceProvider();

    Assert.NotNull(openRouterServices.GetRequiredService<RootAgentResolver>());
  }

  [Fact]
  public void SelectedProvider_WithoutApiKey_Throws()
  {
    AgentSettings settings = Settings(openRouterKey: null);
    using TestAppDatabase db = TestAppDatabase.Create();
    Exception? ex = Record.Exception(() => new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("m", 512, 0.5f, 8192).Value!,
            new AgentHostOptions(
                FixedWorkspaceContext.ForPath("app"), new UnrootedPathResolver()), db.Database));

    InvalidOperationException invalid = Assert.IsType<InvalidOperationException>(ex);
    Assert.Contains("API key", invalid.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void UnknownProviderName_Throws_ArgumentException()
  {
    AgentSettings settings = Settings();
    using TestAppDatabase db = TestAppDatabase.Create();
    Exception? ex = Record.Exception(() => new ServiceCollection()
        .AddEThangAgentCore(settings, "anthropic",
            ModelConfig.Create("m", 512, 0.5f, 8192).Value!,
            new AgentHostOptions(
                FixedWorkspaceContext.ForPath("app"), new UnrootedPathResolver()), db.Database));

    ArgumentException argument = Assert.IsType<ArgumentException>(ex);
    Assert.Contains("anthropic", argument.Message, StringComparison.Ordinal);
  }

  /// <summary>Issue #23: ONE memoized price source per container, shared by the root loop
  ///     and every child run, so a session's rows and its subagents' rows are priced on
  ///     one basis. A container that resolves it twice must get the SAME instance.</summary>
  [Fact]
  public void UsagePriceSource_IsOneSingletonPerContainer()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    using ServiceProvider services = new ServiceCollection()
        .AddEThangAgentCore(Settings(), Providers.OpenRouter,
            ModelConfig.Create("m", 512, 0.5f, 8192).Value!,
            new AgentHostOptions(FixedWorkspaceContext.ForPath("app"), new UnrootedPathResolver()),
            db.Database)
        .BuildServiceProvider();

    IUsagePriceSource first = services.GetRequiredService<IUsagePriceSource>();
    IUsagePriceSource second = services.GetRequiredService<IUsagePriceSource>();

    Assert.Same(first, second);
    _ = Assert.IsType<CatalogUsagePriceSource>(first);
  }
}
