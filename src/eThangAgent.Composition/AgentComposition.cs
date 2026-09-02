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
using eThangAgent.Transport.ACL;
using eThangAgent.Web.ACL;
using eThangAgent.Zai.ACL;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition;

public static class AgentComposition
{
  /// <summary>Registers every host-agnostic piece of the agent: the selected AI
  ///     provider's ACL, the platform ACLs, the agent loop, capability registry,
  ///     stores, nudge policy, system prompts, and session lifecycle. Frontends supply
  ///     exactly three decisions via AgentHostOptions. Registration order and lifetimes
  ///     mirror the CLI composition root this replaces.
  ///     <paramref name="database"/> lets multi-session hosts share ONE app database;
  ///     when omitted each container constructs its own (single-session hosts).</summary>
  /// <exception cref="ArgumentException">providerName is not a known provider id.</exception>
  /// <exception cref="InvalidOperationException">the selected provider has no configured API key.</exception>
  public static IServiceCollection AddEThangAgentCore(this IServiceCollection services,
      AgentSettings settings, string providerName, ModelConfig defaultModel, AgentHostOptions host,
      AppDatabase? database = null, IEnumerable<Message>? conversationSeed = null,
      ProcessMailboxLocator? mailboxLocator = null)
  {
    ArgumentNullException.ThrowIfNull(settings);
    ArgumentNullException.ThrowIfNull(defaultModel);
    ArgumentNullException.ThrowIfNull(host);
    if (!Providers.IsKnown(providerName))
    {
      throw new ArgumentException(
          $"Unknown provider '{providerName}'. Known providers: {Providers.OpenRouter}, {Providers.Zai}.",
          nameof(providerName));
    }

    IServiceCollection wired = AddProviderServices(services, settings, providerName)
        .AddSingleton(defaultModel)
        // Seed hydrates a resumed session's transcript; a fresh session passes null
        // and starts from an empty conversation.
        .AddSingleton(_ => new Conversation(conversationSeed))
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
        .AddSingleton<IWebAccess, HttpWebAccess>()
        .AddSingleton<IHtmlToMarkdown, HtmlAgilityMarkdownConverter>()
        .AddSingleton(sp => new AgentToolsProvider("agent",
        [
            new AgentToolBinding(
                    new ReadTool(sp.GetRequiredService<IPathResolver>(),
                        sp.GetRequiredService<IFileSystemAccess>()),
                    "Read lines from a text file."),
                new AgentToolBinding(
                    new WriteTool(sp.GetRequiredService<IPathResolver>(),
                        sp.GetRequiredService<IFileWriteAccess>()),
                    "Create or overwrite a workspace file."),
                new AgentToolBinding(
                    new WriteMarkdownTool(sp.GetRequiredService<IPathResolver>(),
                        sp.GetRequiredService<IFileWriteAccess>()),
                    "Render a structured JSON document into markdown; return it or write it to a workspace file."),
                new AgentToolBinding(
                    new EditTool(sp.GetRequiredService<IPathResolver>(),
                        sp.GetRequiredService<IFileEditAccess>()),
                    "Edit a file by exact literal replacement."),
                new AgentToolBinding(
                    new SearchTool(sp.GetRequiredService<IPathResolver>(),
                        sp.GetRequiredService<ISearchAccess>()),
                    "Search workspace text files with literal or regex patterns."),
                new AgentToolBinding(
                    new DbSchemaTool(sp.GetRequiredService<ISelfDatabaseAccess>()),
                    "List the tables, columns, and indexes of the agent's own app database."),
                new AgentToolBinding(
                    new DbQueryTool(sp.GetRequiredService<ISelfDatabaseAccess>()),
                    "Run one read-only SQL query against the agent's own app database."),
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
                        sp.GetRequiredService<IGitCommitAccess>(),
                        sp.GetRequiredService<ICommitStyleProvider>()),
                    "Commit the current index with a validated conventional or gitmoji message."),
                new AgentToolBinding(
                    new WebFetchTool(sp.GetRequiredService<IWebAccess>(),
                        sp.GetRequiredService<IHtmlToMarkdown>()),
                    "Fetch a web page or resource over HTTP(S) and return readable text (HTML converted to markdown; other text verbatim)."),
                // Pure graph math, no external access: safe for sub-agents too.
                new AgentToolBinding(
                    new CycleCheckTool(),
                    "Detect dependency cycles in a supplied construction graph and classify deadlock risk."),
                // z.ai capability APIs surface only on z.ai-wired sessions — switching
                // providers is a different experience by design.
                .. ZaiToolBindings(sp, providerName),
        ]))
        .AddSingleton(host.WorkspaceContext)
        .AddSingleton(host.PathResolver)
        .AddSingleton(host.ClarifyChannel)
        // One app-owned database: hosts opening several sessions pass a shared
        // instance here so every session's stores hit the same SQLite file.
        .AddSingleton(_ => database ?? new AppDatabase())
        .AddSingleton<IAppPreferenceStore>(sp => new SqliteAppPreferenceStore(
            sp.GetRequiredService<AppDatabase>()))
        .AddSingleton<ISelfDatabaseAccess, SqliteSelfDatabaseAccess>()
        .AddSingleton<IContextWindowSource, SessionContextWindowSource>()
        .AddSingleton(sp =>
        {
          // Summarizer model resolved per compaction (never pinned at startup).
          CompactionModelResolver summarizer = new(
              sp.GetRequiredService<IAppPreferenceStore>(),
              sp.GetRequiredService<IModelCatalog>(),
              providerName,
              sp.GetRequiredService<IWorkspaceContext>().WorkspaceId);
          return new DefaultContextCompactor(
              sp.GetRequiredService<IModelProviderFactory>(),
              () => summarizer.ResolveAsync(SubAgentSpawner.ChildMaxTokens, SubAgentSpawner.ChildTemperature).GetAwaiter().GetResult());
        })
        .AddSingleton<ICommitStyleProvider, AppPreferenceCommitStyleProvider>()
        .AddSingleton<IStateStore, SqliteStateStore>()
        .AddSingleton<IAgentStore, SqliteAgentStore>()
        .AddSingleton<IAgentHeartbeat>(_ => new InMemoryAgentHeartbeat(TimeProvider.System))
        .AddSingleton<IWatchdogEventStore>(sp => new SqliteWatchdogEventStore(
            sp.GetRequiredService<AppDatabase>()))
        .AddSingleton<ISkillCatalog, EmbeddedSkillCatalog>()
        .AddSingleton<ILearnedSkillStore, SqliteLearnedSkillStore>()
        .AddSingleton<Func<DateTimeOffset>>(_ => () => DateTimeOffset.UtcNow)
        .AddSingleton<SqliteCuratedMemoryStore>()
        .AddSingleton<ICuratedMemoryStore>(sp => sp.GetRequiredService<SqliteCuratedMemoryStore>())
        .AddSingleton<IAgentInbox, BoundedAgentMailbox>()
        .AddSingleton<IMailboxStore, SqliteMailboxStore>()
        .AddSingleton<IAgentEvents, InProcessAgentEvents>()
        .AddSingleton<ChildSupervisorRegistry>()
        .AddSingleton<ChildMailboxRegistry>()
        // W3.2: the cross-container mailbox locator. Hosts opening several sessions pass
        // ONE shared instance (AgentSessionFactory does), so every container's provider
        // consults the same process-wide registry of live mailboxes; a lone container
        // gets its own, which changes nothing (its source is itself).
        .AddSingleton(_ => mailboxLocator ?? new ProcessMailboxLocator())
        .AddSingleton<SessionMemoryWriteCounter>()
        .AddSingleton<INudgePolicy>(_ => new DefaultNudgePolicy())
        ;

    // Only OpenRouter wires the two-stage intelligent selector: z.ai sessions run no
    // automatic selection — the user picks glm-5.3 or glm-5.3-flash through the host's
    // model picker, so a selector there would only burn tokens. Consumers treat a
    // missing selector as "serve the fallback / preference" (RootAgentResolver,
    // StartSpawnHandler).
    wired = AddModelServices(wired, providerName);
    if (providerName == Providers.OpenRouter)
    {
      wired = wired.AddSingleton<IModelSelector>(sp => new IntelligentModelSelector(
          sp.GetRequiredService<IModelProvider>(),
          sp.GetRequiredService<IModelCatalog>(),
          ModelConfig.Create(Providers.SelectorModelId(providerName), null, 2048, 0f,
              // Bootstrap-only selector pseudo-model; its own calls are tiny and fixed.
              Providers.RoutingContextWindow).Value!));
    }

    wired = wired
        .AddSingleton(sp => new SubAgentServices(
            sp.GetRequiredService<IModelProviderFactory>(),
            sp.GetRequiredService<IAgentStore>(),
            sp.GetRequiredService<IToolRegistry>(),
            sp.GetRequiredService<ISystemPromptProvider>(),
            sp.GetRequiredService<SubAgentOptions>(),
            sp.GetRequiredService<IAgentHeartbeat>(),
            sp.GetRequiredService<IAgentEvents>(),
            sp.GetRequiredService<IWatchdogEventStore>(),
            InboxFor: id => sp.GetRequiredService<ChildMailboxRegistry>().InboxFor(id)))
        .AddSingleton(sp => new SubAgentSpawner(
            sp.GetRequiredService<SubAgentServices>(),
            sp.GetRequiredService<SessionModelPreferences>(),
            sp.GetRequiredService<IContextWindowSource>(),
            sp.GetRequiredService<DefaultContextCompactor>()))
        .AddSingleton(sp => new InProcessAgentRuntime(
            sp.GetRequiredService<SubAgentSpawner>(),
            sp.GetRequiredService<IAgentStore>(),
            settings.SubAgents.MaxConcurrentAgents,
            sp.GetRequiredService<IMailboxStore>(),
            sp.GetRequiredService<IAgentEvents>(),
            sp.GetRequiredService<ChildSupervisorRegistry>(),
            sp.GetRequiredService<ChildMailboxRegistry>()))
        .AddSingleton<IAgentRuntime>(sp => sp.GetRequiredService<InProcessAgentRuntime>())
        .AddSingleton<IAgentSpawnCommand>(sp => new StartSpawnHandler(
            sp.GetRequiredService<IAgentStore>(),
            sp.GetRequiredService<IAgentRuntime>(),
            sp.GetRequiredService<SubAgentOptions>(),
            new SpawnOptions(
                Providers.FallbackModelId(providerName),
                sp.GetRequiredService<SessionModelPreferences>(),
                ChildToolSurface: ChildToolSurface(sp)),
            sp.GetService<IModelSelector>(),
            sp.GetRequiredService<IContextWindowSource>()))
        .AddSingleton<IAgentQueries, AgentQueries>()
        .AddSingleton<IMemoryRecallQuery, RecallQueryHandler>()
        .AddSingleton<IMemorySessionsQuery, SessionsQueryHandler>()
        .AddSingleton(sp => new AgentLinkRegistry(
            new SqliteLinkStore(sp.GetRequiredService<AppDatabase>()),
            () => sp.GetRequiredService<IWorkspaceContext>().WorkspaceId))
        .AddSingleton<SpawnGraphHandler>()
        .AddSingleton(sp =>
        {
          AgentRecord rootRecord = AgentRecord.Spawned(AgentId.NewId(), null, 0,
                  sp.GetRequiredService<ModelConfig>().ModelId, null,
                  "root session", DateTimeOffset.UtcNow);
          SpawnGraphHandler graph = sp.GetRequiredService<SpawnGraphHandler>();
          return new AgentCapabilityProvider(
                  sp.GetRequiredService<IAgentSpawnCommand>(),
                  sp.GetRequiredService<IAgentQueries>(),
                  () => SubAgentSpawner.RunningChild ?? rootRecord,
                  sp.GetRequiredService<IAgentRuntime>(),
                  sp.GetRequiredService<AgentLinkRegistry>(),
                  locator: sp.GetRequiredService<ProcessMailboxLocator>(),
                  eventsFor: id => sp.GetRequiredService<ProcessMailboxLocator>().EventsFor(id),
                  fanout: async (parent, children, ct) =>
                  {
                    Result<SpawnGraphOutcome> joined = await graph.ExecuteAsync(parent,
                        new SpawnGraphRequest(Label: "", Children: children, Join: new JoinPolicy(false)), ct).ConfigureAwait(false);
                    return joined.IsSuccess
                        ? joined.Value.Render()
                        : "Error [" + joined.Error.Code + "]: " + joined.Error.Message;
                  });
        })
        .AddSingleton(_ => EvidenceOptions.Default)
        .AddSingleton<IEvidenceRunner, CSharpEvidenceRunner>()
        .AddSingleton<IStateService, StateService>()
        .AddSingleton<IProviderExclusionStore>(sp => new SqliteProviderExclusionStore(
            sp.GetRequiredService<AppDatabase>(),
            sp.GetRequiredService<IWorkspaceContext>()))
        .AddSingleton<StateCapabilityProvider>()
        .AddSingleton<MemoryCapabilityProvider>()
        .AddSingleton(sp => new CuratedMemoryCapabilityProvider(
            sp.GetRequiredService<ICuratedMemoryStore>(),
            () => sp.GetRequiredService<IWorkspaceContext>().WorkspaceId,
            () => SubAgentSpawner.RunningChild?.Id.ToString(),
            sp.GetRequiredService<SessionMemoryWriteCounter>().Increment,
            () => DateTimeOffset.UtcNow))
        .AddSingleton<ICapabilityRegistry>(sp =>
            CapabilityRegistry.Create(AgentSurface(sp, sp.GetRequiredService<AgentToolsProvider>())))
        // MUST stay lazy inside this closure: the agent surface reaches back to
        // IExecEngine (agent -> spawn -> tool registry -> exec tool), so building any
        // registry eagerly here would re-enter this not-yet-finished singleton and
        // park forever on the container's in-progress slot (TLC-proven deadlock,
        // DiResolution.tla). Deferred like the Lazy<> wiring this replaced.
        .AddSingleton<Func<ICapabilityRegistry>>(sp =>
        {
          Lazy<AgentToolsProvider> tools = new(() =>
                  // Human-facing actions never reach sub-agents: clarify blocks on the
                  // user, and a machine-owned child must neither wait on nor interrupt them.
                  sp.GetRequiredService<AgentToolsProvider>().Except(HumanFacingActions));
          Lazy<ICapabilityRegistry> root = new(() => CapabilityRegistry.Create(
                  AgentSurface(sp, sp.GetRequiredService<AgentToolsProvider>())));
          Lazy<ICapabilityRegistry> child = new(() => CapabilityRegistry.Create(
                  AgentSurface(sp, tools.Value)));

          // Dispatch-time grant enforcement on the exec path (R1): a running child with
          // a resolved grant set sees the child surface FILTERED to that set. Resolved
          // per execution — the ambient RunningChild flips between containers.
          ICapabilityRegistry ResolveSurface()
          {
            AgentRecord? running = SubAgentSpawner.RunningChild;
            ICapabilityRegistry surface = running is null ? root.Value : child.Value;
            if (running?.Contract is { } contractJson
                && SpawnContract.Decode(contractJson).DecodedEffectiveTools is { } effective)
            {
              AgentId childId = running.Id;
              surface = new FilteredCapabilityRegistry(surface, effective,
                  onDenial: name => AuditGrantDenial(sp, childId, name));
            }

            return surface;
          }

          return () => ResolveSurface();
        })
        .AddSingleton<IExecEngine>(sp => new CSharpScriptExecEngine(
            sp.GetRequiredService<Func<ICapabilityRegistry>>(),
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
            new SkillsBootstrapPromptProvider(sp.GetRequiredService<ISkillCatalog>(),
                sp.GetRequiredService<ICommitStyleProvider>()),
                new StaticPromptProvider(
                    "You are eThang Agent, an AI coding agent for Windows. Work in the current " +
                    "workspace, prefer the provided tools over guessing, and keep responses tight."),
                new ExecGuidePromptProvider(
                    new Lazy<ICapabilityRegistry>(() => sp.GetRequiredService<ICapabilityRegistry>())),
                new CuratedMemoryGuidePromptProvider(),
                ..host.ExtraPromptProviders,
        ]))
        .AddSingleton(subAgents(settings, defaultModel.ModelId))
        // Root agent is built lazily on the first turn (and rebuilt on every model
        // reselection) by RootAgentHolder; the root is NOT known at container build time
        // while intelligent selection is active. The holder reuses the shared
        // Conversation/provider/tools/system-prompt so a rebuild preserves all message history.
        .AddSingleton<SessionModelPreferences>()
        .AddSingleton<RootSessionIdentity>()
        .AddSingleton(sp => new RootAgentHolder(
            sp.GetRequiredService<IModelProvider>(),
            sp.GetRequiredService<Conversation>(),
            sp.GetRequiredService<IToolRegistry>(),
            sp.GetRequiredService<ISystemPromptProvider>(),
            contextCompactor: sp.GetRequiredService<DefaultContextCompactor>()))
        .AddSingleton(sp => new RootAgentResolver(
            new RootModelContext(
                sp.GetRequiredService<IAgentStore>(),
                sp.GetRequiredService<RootSessionIdentity>(),
                Providers.FallbackModelId(providerName),
                defaultModel.MaxTokens,
                defaultModel.Temperature,
                sp.GetRequiredService<IContextWindowSource>()),
            sp.GetService<IModelSelector>(),
            sp.GetRequiredService<SessionModelPreferences>()))
        .AddSingleton(sp => new ProviderFailoverResolver(
            new RootModelContext(
                sp.GetRequiredService<IAgentStore>(),
                sp.GetRequiredService<RootSessionIdentity>(),
                Providers.FallbackModelId(providerName),
                defaultModel.MaxTokens,
                defaultModel.Temperature,
                sp.GetRequiredService<IContextWindowSource>()),
            sp.GetRequiredService<IProviderExclusionStore>(),
            sp.GetService<IModelSelector>()))
        .AddSingleton(sp => new SendMessageCommandHandler(
            agent: null,
            sp.GetRequiredService<Conversation>(),
            sp.GetRequiredService<INudgePolicy>(),
            () => sp.GetRequiredService<SessionMemoryWriteCounter>().Count,
            sp.GetRequiredService<IAgentInbox>(),
            sp.GetRequiredService<RootAgentHolder>(),
            sp.GetRequiredService<RootAgentResolver>()))
        .AddSingleton<RootSessionLifecycle>()
        ;

    // Runtime selection (R3.4): in-process by default; RemoteHost = true routes child
    // starts through the out-of-process ChildHost under RemoteHostSupervisor.
    if (settings.RemoteHost)
    {
      wired = wired
          .AddSingleton(sp => new RemoteHostSupervisor(
              sp.GetRequiredService<IWorkspaceContext>().WorkspaceId,
              Path.Combine(Path.GetTempPath(), "ethang-agent", sp.GetRequiredService<IWorkspaceContext>().WorkspaceId),
              settings,
              sp.GetRequiredService<AppDatabase>().DatabasePath,
              // Host-health notices surface on the session transcript when the host UI
              // has attached its notice sink; headless hosts drop them.
              notice => sp.GetRequiredService<AgentSession>().PostNotice(notice)))
          // The runtime starts DISCONNECTED: RepairOrphansAsync -> AttachAsync owns
          // connecting (once), starting the pump, and reading the declared live set.
          // A connection attempt at registration would race the host's single accept.
          .AddSingleton<RemoteAgentRuntime>()
          .AddSingleton<IAgentRuntime>(sp => sp.GetRequiredService<RemoteAgentRuntime>());
    }

    return wired;
  }

  /// <summary>Wires the EXCLUSIVELY selected provider's chat transport: configuration,
  ///     a named HttpClient, and the session's single IModelProvider typed client. Only
  ///     one provider is ever registered per container — switching providers means
  ///     opening a session wired for the other one.</summary>
  private static IServiceCollection AddProviderServices(
      IServiceCollection services, AgentSettings settings, string providerName)
  {
    string apiKey = providerName switch
    {
      Providers.OpenRouter => settings.OpenRouter.ApiKey,
      Providers.Zai => settings.Zai.ApiKey,
      _ => throw new ArgumentOutOfRangeException(nameof(providerName), providerName, "Unknown provider id.")
    } ?? throw new InvalidOperationException(
        $"Provider '{providerName}' is selected but its API key is not configured. " +
        "Add the key under Settings (gear icon) before opening a session with it.");

    return providerName == Providers.OpenRouter
        ? services
            .AddSingleton(new OpenRouterConfiguration(apiKey, settings.OpenRouter.BaseUrl))
            .AddHttpClient("OpenRouter", client => { client.Timeout = TimeSpan.FromSeconds(120); })
            .Services
            .AddHttpClient<IModelProvider, OpenRouterModelProvider>(client =>
            {
              client.Timeout = TimeSpan.FromSeconds(120);
            })
            .Services
        : services
            .AddSingleton(new ZaiConfiguration(apiKey, settings.Zai.BaseUrl)
            {
              EndpointMode = settings.Zai.EndpointMode
            })
            .AddHttpClient("Zai", client => { client.Timeout = TimeSpan.FromSeconds(120); })
            .Services
            .AddHttpClient<IModelProvider, ZaiModelProvider>(client =>
            {
              client.Timeout = TimeSpan.FromSeconds(120);
            })
            .Services;
  }

  /// <summary>Wires the selected provider's model factory and catalog. z.ai has no
  ///     models-listing endpoint, so its catalog is the static curated one.</summary>
  private static IServiceCollection AddModelServices(IServiceCollection services, string providerName)
  {
    return providerName == Providers.OpenRouter
        ? services
            .AddSingleton<IModelProviderFactory>(sp => new OpenRouterModelProviderFactory(
                sp.GetRequiredService<OpenRouterConfiguration>(),
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("OpenRouter")))
            .AddSingleton<IModelCatalog>(sp => new OpenRouterCatalogClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("OpenRouter"),
                sp.GetRequiredService<OpenRouterConfiguration>()))
        : services
            .AddSingleton<IModelProviderFactory>(sp => new ZaiModelProviderFactory(
                sp.GetRequiredService<ZaiConfiguration>(),
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("Zai")))
            .AddSingleton<IModelCatalog>(_ => new ZaiModelCatalog());
  }

  /// <summary>Actions only a root agent may invoke: they present UI to the human,
  ///     and a machine-owned child must never block on (or interrupt) the user.</summary>
  private static readonly string[] HumanFacingActions = ["clarify"];

  /// <summary>Best-effort exec-path grant audit (R1.4): a denied dispatch lands as a
  ///     GrantViolation watchdog row; a failing write never blocks the denial.</summary>
  private static void AuditGrantDenial(IServiceProvider sp, AgentId childId, string actionName)
  {
    _ = Task.Run(async () =>
    {
      try
      {
        _ = await sp.GetRequiredService<IWatchdogEventStore>().AppendAsync(new WatchdogEvent(
            Guid.NewGuid(), childId, WatchdogEventKind.GrantViolation,
            "action '" + actionName + "' denied at exec dispatch", 0, null, DateTimeOffset.UtcNow)).ConfigureAwait(false);
      }
      // Named decision (CA1031): audit is best-effort by contract — a failed event
      // write never blocks the dispatch refusal it records.
#pragma warning disable CA1031 // Do not catch general exception types
      catch
      {
        // Swallowed deliberately: see the named decision above.
      }
#pragma warning restore CA1031
    });
  }

  /// <summary>The session's child tool surface (R1): the action ids a default child can
  ///     dispatch — the loop registry's tools plus every child-surface capability action
  ///     (agent tools minus human-facing, agent.* actions, state, memory, curated).
  ///     Built lazily: the surface reaches IExecEngine through the capability closure,
  ///     so eager construction re-enters the container (see the registry-factory
  ///     registration comment).</summary>
  private static HashSet<string> ChildToolSurface(IServiceProvider sp)
  {
    // Deliberately resolves NO capability provider here: the SpawnOptions factory runs
    // inside AgentCapabilityProvider's own construction, so resolving it (or the
    // capability registry) would re-enter the singleton in flight. The agent actions
    // are the provider's fixed set, named literally; everything else is provider-level.
    AgentToolsProvider childTools = sp.GetRequiredService<AgentToolsProvider>().Except(HumanFacingActions);
    IEnumerable<string> names = childTools.Actions.Select(a => a.Name)
        .Concat(AgentCapabilityProvider.ActionNames)
        .Concat(sp.GetRequiredService<StateCapabilityProvider>().Actions.Select(a => a.Name))
        .Concat(sp.GetRequiredService<MemoryCapabilityProvider>().Actions.Select(a => a.Name))
        .Concat(sp.GetRequiredService<CuratedMemoryCapabilityProvider>().Actions.Select(a => a.Name));
    return [.. names];
  }

  /// <summary>The z.ai capability-API tools, bound only when the session is wired for
  ///     z.ai in GeneralApi endpoint mode — web search, page reading, token counting,
  ///     image generation, document OCR, and audio transcription all reach the platform
  ///     through one shared client. The capability APIs exist only on the general
  ///     pay-as-you-go endpoint, so CodingPlan sessions carry none of them.</summary>
  private static IEnumerable<AgentToolBinding> ZaiToolBindings(IServiceProvider sp, string providerName)
  {
    if (providerName != Providers.Zai)
    {
      yield break;
    }

    ZaiConfiguration config = sp.GetRequiredService<ZaiConfiguration>();
    if (config.EndpointMode != ZaiEndpointMode.GeneralApi)
    {
      yield break;
    }

    HttpClient http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("Zai");
    yield return new AgentToolBinding(
        new ZaiWebSearchTool(http, config),
        "Search the live web (z.ai).");
    yield return new AgentToolBinding(
        new ZaiWebReaderTool(http, config),
        "Fetch one web page as markdown (z.ai reader).");
    yield return new AgentToolBinding(
        new ZaiTokenizerTool(http, config),
        "Count GLM tokens for a piece of text.");
    yield return new AgentToolBinding(
        new ZaiImageTool(http, config,
            sp.GetRequiredService<IPathResolver>(), sp.GetRequiredService<IFileWriteAccess>()),
        "Generate an image and save it as a workspace PNG.");
    yield return new AgentToolBinding(
        new ZaiOcrTool(http, config,
            sp.GetRequiredService<IPathResolver>(), sp.GetRequiredService<IFileSystemAccess>()),
        "Transcribe a workspace PDF or image to markdown.");
    yield return new AgentToolBinding(
        new ZaiTranscriptionTool(http, config,
            sp.GetRequiredService<IPathResolver>(), sp.GetRequiredService<IFileSystemAccess>()),
        "Transcribe a short workspace audio clip.");
  }

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
        sp.GetRequiredService<CuratedMemoryCapabilityProvider>(),
  ];

  /// <summary>Child-agent options with the default-model fallback applied: when
  ///     configuration omits the SubAgent DefaultModel key, children inherit the host's
  ///     root model rather than failing every spawn with MissingModel. A configured
  ///     value always wins; empty config values are still rejected upstream at bind time.</summary>
  private static SubAgentOptions subAgents(AgentSettings settings, string rootModelId)
      => string.IsNullOrWhiteSpace(settings.SubAgents.DefaultModel)
          ? new SubAgentOptions(rootModelId,
              settings.SubAgents.MaxConcurrentAgents, settings.SubAgents.MaxDepth)
          : settings.SubAgents;
}
