using Ag = eThangAgent.AgentDomain.Agent;
using eThangAgent.Agent.Application;
using eThangAgent.Agent.Application.Memory;
using eThangAgent.Agent.Application.Nudges;
using eThangAgent.AgentDomain;
using eThangAgent.AgentInfrastructure;
using eThangAgent.CapabilityDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.FileSystem.ACL;
using eThangAgent.MemoryDomain;
using eThangAgent.ModelDomain;
using eThangAgent.OpenRouter.ACL;
using eThangAgent.Roslyn.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;
using eThangAgent.StateDomain;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition;

public static class AgentComposition
{
    /// <summary>Registers every host-agnostic piece of the agent: OpenRouter and platform
    ///     ACLs, the agent loop, capability registry, stores, nudge policy, system prompts,
    ///     and session lifecycle. Frontends supply exactly three decisions via AgentHostOptions.
    ///     Registration order and lifetimes mirror the CLI composition root this replaces.
    ///     <paramref name="database"/> lets multi-session hosts share ONE app database;
    ///     when omitted each container constructs its own (single-session hosts).</summary>
    public static IServiceCollection AddEThangAgentCore(this IServiceCollection services,
        AgentSettings settings, string apiKey, ModelConfig defaultModel, AgentHostOptions host,
        AppDatabase? database = null)
    {
        return services
            .AddSingleton(new OpenRouterConfiguration(apiKey, settings.BaseUrl))
            .AddHttpClient("OpenRouter", client => { client.Timeout = TimeSpan.FromSeconds(120); })
            .Services
            .AddHttpClient<IModelProvider, OpenRouterModelProvider>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(120);
            })
            .Services
            .AddSingleton(defaultModel)
            .AddSingleton<Conversation>()
            .AddSingleton<IConversationRepository, InMemoryConversationRepository>()
            .AddSingleton<DirectFileSystemAccess>()
            .AddSingleton<IFileSystemAccess>(sp => sp.GetRequiredService<DirectFileSystemAccess>())
            .AddSingleton<IFileWriteAccess>(sp => sp.GetRequiredService<DirectFileSystemAccess>())
            .AddSingleton<IFileEditAccess>(sp => sp.GetRequiredService<DirectFileSystemAccess>())
            .AddSingleton<ISearchAccess>(sp => sp.GetRequiredService<DirectFileSystemAccess>())
            .AddSingleton<DirectGitAccess>()
            .AddSingleton<IGitQueryAccess>(sp => sp.GetRequiredService<DirectGitAccess>())
            .AddSingleton<IGitCommitAccess>(sp => sp.GetRequiredService<DirectGitAccess>())
            .AddSingleton(ExecOptions.Default)
            .AddSingleton<IExecOutputStore>(_ => new ExecArtifactStore())
            .AddSingleton<IExecActivitySink>(_ => NullExecActivitySink.Instance)
            .AddSingleton(sp => new AgentToolsProvider("agent",
            [
                new AgentToolBinding(
                    new ReadTool(sp.GetRequiredService<IFileSystemAccess>()),
                    "Read lines from a text file."),
                new AgentToolBinding(
                    new WriteTool(sp.GetRequiredService<IPathResolver>(),
                        sp.GetRequiredService<IFileWriteAccess>()),
                    "Create or overwrite a workspace file."),
                new AgentToolBinding(
                    new EditTool(sp.GetRequiredService<IPathResolver>(),
                        sp.GetRequiredService<IFileEditAccess>()),
                    "Edit a file by exact literal replacement."),
                new AgentToolBinding(
                    new SearchTool(sp.GetRequiredService<IPathResolver>(),
                        sp.GetRequiredService<ISearchAccess>()),
                    "Search workspace text files with literal or regex patterns."),
                new AgentToolBinding(
                    new SkillListTool(sp.GetRequiredService<ISkillCatalog>(),
                        sp.GetRequiredService<ILearnedSkillStore>()),
                    "List available skills."),
                new AgentToolBinding(
                    new SkillViewTool(sp.GetRequiredService<ISkillCatalog>(),
                        sp.GetRequiredService<ILearnedSkillStore>()),
                    "Load a skill's full content by name."),
                new AgentToolBinding(
                    new SkillManageTool(sp.GetRequiredService<ISkillCatalog>(),
                        sp.GetRequiredService<ILearnedSkillStore>(),
                        sp.GetRequiredService<Func<DateTimeOffset>>()),
                    "Create, update, or delete learned skills."),
                new AgentToolBinding(
                    new ClarifyTool(sp.GetRequiredService<IClarifyChannel>()),
                    "Ask the human a clarifying question with structured options."),
                new AgentToolBinding(
                    new TodoTool(new StateServiceTodoListStore(sp.GetRequiredService<IStateService>())),
                    "Track a workspace task list."),
                new AgentToolBinding(
                    new GitStatusTool(sp.GetRequiredService<IPathResolver>(),
                        sp.GetRequiredService<IGitQueryAccess>()),
                    "Show branch and working-tree status."),
                new AgentToolBinding(
                    new WorkingDiffTool(sp.GetRequiredService<IPathResolver>(),
                        sp.GetRequiredService<IGitQueryAccess>()),
                    "Show staged/unstaged/all working-tree diff, bounded."),
                new AgentToolBinding(
                    new GitCommitTool(sp.GetRequiredService<IPathResolver>(),
                        sp.GetRequiredService<IGitCommitAccess>()),
                    "Commit the current index with a validated conventional or gitmoji message."),
            ]))
            .AddSingleton(host.WorkspaceContext)
            .AddSingleton(host.PathResolver)
            .AddSingleton(host.ClarifyChannel)
            // One app-owned database: hosts opening several sessions pass a shared
            // instance here so every session's stores hit the same SQLite file.
            .AddSingleton(_ => database ?? new AppDatabase())
            .AddSingleton<IStateStore, SqliteStateStore>()
            .AddSingleton<IAgentStore, SqliteAgentStore>()
            .AddSingleton<ISkillCatalog, EmbeddedSkillCatalog>()
            .AddSingleton<ILearnedSkillStore, SqliteLearnedSkillStore>()
            .AddSingleton<Func<DateTimeOffset>>(_ => () => DateTimeOffset.UtcNow)
            .AddSingleton<SqliteCuratedMemoryStore>()
            .AddSingleton<ICuratedMemoryStore>(sp => sp.GetRequiredService<SqliteCuratedMemoryStore>())
            .AddSingleton<SessionMemoryWriteCounter>()
            .AddSingleton<INudgePolicy>(_ => new DefaultNudgePolicy(() => DateTimeOffset.UtcNow))
            .AddSingleton<IModelProviderFactory>(sp => new OpenRouterModelProviderFactory(
                sp.GetRequiredService<OpenRouterConfiguration>(),
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("OpenRouter")))
            .AddSingleton<SubAgentSpawner>()
            .AddSingleton<IAgentRuntime>(sp => new InProcessAgentRuntime(
                sp.GetRequiredService<SubAgentSpawner>(),
                sp.GetRequiredService<IAgentStore>(),
                settings.SubAgents.MaxConcurrentAgents))
            .AddSingleton<IAgentSpawnCommand, StartSpawnHandler>()
            .AddSingleton<IAgentQueries, AgentQueries>()
            .AddSingleton<IMemoryRecallQuery, RecallQueryHandler>()
            .AddSingleton<IMemorySessionsQuery, SessionsQueryHandler>()
            .AddSingleton<AgentCapabilityProvider>(sp =>
            {
                var rootRecord = AgentRecord.Spawned(AgentId.NewId(), null, 0,
                    sp.GetRequiredService<ModelConfig>().ModelId, null,
                    "root session", DateTimeOffset.UtcNow);
                return new AgentCapabilityProvider(
                    sp.GetRequiredService<IAgentSpawnCommand>(),
                    sp.GetRequiredService<IAgentQueries>(),
                    () => SubAgentSpawner.RunningChild ?? rootRecord);
            })
            .AddSingleton<EvidenceOptions>(_ => EvidenceOptions.Default)
            .AddSingleton<IEvidenceRunner, CSharpEvidenceRunner>()
            .AddSingleton<IStateService, StateService>()
            .AddSingleton<StateCapabilityProvider>()
            .AddSingleton<MemoryCapabilityProvider>()
            .AddSingleton<ICapabilityRegistry>(sp =>
                CapabilityRegistry.Create(AgentSurface(sp, sp.GetRequiredService<AgentToolsProvider>())))
            // MUST stay lazy inside this closure: the agent surface reaches back to
            // IExecEngine (agent -> spawn -> tool registry -> exec tool), so building any
            // registry eagerly here would re-enter this not-yet-finished singleton and
            // park forever on the container's in-progress slot (TLC-proven deadlock,
            // DiResolution.tla). Deferred like the Lazy<> wiring this replaced.
            .AddSingleton<Func<ICapabilityRegistry>>(sp =>
            {
                var tools = new Lazy<AgentToolsProvider>(() =>
                    // Human-facing actions never reach sub-agents: clarify blocks on the
                    // user, and a machine-owned child must neither wait on nor interrupt them.
                    sp.GetRequiredService<AgentToolsProvider>().Except(HumanFacingActions));
                var root = new Lazy<ICapabilityRegistry>(() => CapabilityRegistry.Create(
                    AgentSurface(sp, sp.GetRequiredService<AgentToolsProvider>())));
                var child = new Lazy<ICapabilityRegistry>(() => CapabilityRegistry.Create(
                    AgentSurface(sp, tools.Value)));
                return () => SubAgentSpawner.RunningChild is null ? root.Value : child.Value;
            })
            .AddSingleton<IExecEngine>(sp => new CSharpScriptExecEngine(
                sp.GetRequiredService<Func<ICapabilityRegistry>>(),
                sp.GetRequiredService<ExecOptions>(),
                // Registry and workspace are both resolved per execution so concurrent
                // sessions in one process each see their own context, never a stale
                // construction-time value pinned to whichever container was built first.
                () => sp.GetRequiredService<IWorkspaceContext>().WorkspaceId))
            .AddSingleton<ITool>(sp => new ExecTool(
                sp.GetRequiredService<IExecEngine>(),
                sp.GetRequiredService<ExecOptions>(),
                sp.GetRequiredService<IExecOutputStore>(),
                sp.GetRequiredService<IExecActivitySink>()))
            .AddSingleton<IToolRegistry>(sp =>
                new ToolRegistry([sp.GetRequiredService<ITool>()]))
            .AddSingleton<ISystemPromptProvider>(sp => new CompositeSystemPromptProvider(
            [
                new SkillsBootstrapPromptProvider(sp.GetRequiredService<ISkillCatalog>()),
                new StaticPromptProvider(
                    "You are eThang Agent, an AI coding agent for Windows. Work in the current " +
                    "workspace, prefer the provided tools over guessing, and keep responses tight."),
                new ExecGuidePromptProvider(
                    new Lazy<ICapabilityRegistry>(() => sp.GetRequiredService<ICapabilityRegistry>())),
                new CuratedMemoryGuidePromptProvider(),
                ..host.ExtraPromptProviders,
            ]))
            .AddSingleton(subAgents(settings, defaultModel.ModelId))
            .AddSingleton<Ag>(sp =>
            {
                var provider = sp.GetRequiredService<IModelProvider>();
                var conversation = sp.GetRequiredService<Conversation>();
                var config = sp.GetRequiredService<ModelConfig>();
                var tools = sp.GetRequiredService<IToolRegistry>();
                return new Ag(provider, conversation, config, tools,
                    sp.GetRequiredService<ISystemPromptProvider>());
            })
            .AddSingleton(sp => new SendMessageCommandHandler(
                sp.GetRequiredService<Ag>(),
                sp.GetRequiredService<Conversation>(),
                sp.GetRequiredService<INudgePolicy>(),
                () => sp.GetRequiredService<SessionMemoryWriteCounter>().Count))
            .AddSingleton<RootSessionLifecycle>()
            ;
    }

    /// <summary>Actions only a root agent may invoke: they present UI to the human,
    ///     and a machine-owned child must never block on (or interrupt) the user.</summary>
    private static readonly string[] HumanFacingActions = ["clarify"];

    /// <summary>The capability providers every agent surface shares, parameterized by the
    ///     agent-tools provider so root and child surfaces differ only in human actions.</summary>
    private static IReadOnlyList<ICapabilityProvider> AgentSurface(
        IServiceProvider sp, AgentToolsProvider tools) =>
    [
        new MergedCapabilityProvider("agent",
        [
            tools,
            sp.GetRequiredService<AgentCapabilityProvider>(),
        ]),
        sp.GetRequiredService<StateCapabilityProvider>(),
        sp.GetRequiredService<MemoryCapabilityProvider>(),
        new CuratedMemoryCapabilityProvider(
            sp.GetRequiredService<ICuratedMemoryStore>(),
            () => sp.GetRequiredService<IWorkspaceContext>().WorkspaceId,
            () => SubAgentSpawner.RunningChild?.Id.ToString(),
            sp.GetRequiredService<SessionMemoryWriteCounter>().Increment,
            () => DateTimeOffset.UtcNow),
    ];

    /// <summary>Child-agent options with the default-model fallback applied: when
    ///     configuration omits the SubAgent DefaultModel key, children inherit the host's
    ///     root model rather than failing every spawn with MissingModel. A configured
    ///     value always wins; empty config values are still rejected upstream at bind time.</summary>
    private static SubAgentOptions subAgents(AgentSettings settings, string rootModelId)
        => string.IsNullOrWhiteSpace(settings.SubAgents.DefaultModel)
            ? new SubAgentOptions(rootModelId, settings.SubAgents.ChildTimeout,
                settings.SubAgents.MaxConcurrentAgents, settings.SubAgents.MaxDepth)
            : settings.SubAgents;
}
