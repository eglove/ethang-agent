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
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

public class CompositionGuardTests
{
  [Fact]
  public void SubAgentDefaultModel_FallsBackToRootModel_WhenConfigOmitsIt()
  {
    AgentSettings settings = new("sk-or-test", new Uri("https://openrouter.test"),
        new SubAgentOptions(null, TimeSpan.FromSeconds(300), 2));
    using ServiceProvider services = new ServiceCollection()
        .AddEThangAgentCore(settings, settings.ApiKey!,
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
    AgentSettings settings = new("sk-or-test", new Uri("https://openrouter.test"),
        new SubAgentOptions(null, TimeSpan.FromSeconds(300), 2));
    using ServiceProvider services = new ServiceCollection()
        .AddEThangAgentCore(settings, settings.ApiKey!,
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

  private sealed class StubClarifyChannel : IClarifyChannel
  {
    public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default)
        => Task.FromResult(Result.Success("1"));
  }
}
