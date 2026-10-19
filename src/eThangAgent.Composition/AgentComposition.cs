using System.Collections.Concurrent;
using eThangAgent.Agent.Application;
using eThangAgent.Agent.Application.Memory;
using eThangAgent.Agent.Application.Nudges;
using eThangAgent.AgentDomain;
using eThangAgent.AgentInfrastructure;
using eThangAgent.CapabilityDomain;
using eThangAgent.ComputerUse.ACL;
using eThangAgent.ConversationDomain;
using eThangAgent.Documents.ACL;
using eThangAgent.FileSystem.ACL;
using eThangAgent.Mcp.ACL;
using eThangAgent.MemoryDomain;
using eThangAgent.ModelDomain;
using eThangAgent.OpenRouter.ACL;
using eThangAgent.PDF.ACL;
using eThangAgent.PlanDomain;
using eThangAgent.Roslyn.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;
using eThangAgent.StateDomain;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using eThangAgent.ToolDomain.Mcp;
using eThangAgent.ToolDomain.Verification;
using eThangAgent.Transport.ACL;
using eThangAgent.Web.ACL;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition;

/// <summary>The optional per-container overrides a host may supply to
///     <see cref="AgentComposition.AddEThangAgentCore"/> beyond the shared database: a
///     conversation seed, a process-wide mailbox locator, and the computer-use access
///     provider. Grouped so the composition entry point keeps a readable parameter list;
///     every member is optional and its absence preserves the single-session default.</summary>
public sealed record CompositionOverrides
{
  /// <summary>Messages the session conversation starts from.</summary>
  public IEnumerable<Message>? ConversationSeed { get; init; }

  /// <summary>The process-wide mailbox locator for cross-container delivery.</summary>
  public ProcessMailboxLocator? MailboxLocator { get; init; }

  /// <summary>The computer-use access provider; null disables the computer tool.</summary>
  public IComputerAccessProvider? ComputerAccessProvider { get; init; }
}

public static class AgentComposition
{
  /// <summary>Registers every host-agnostic piece of the agent: the selected AI
  ///     provider's ACL, the platform ACLs, the agent loop, capability registry,
  ///     stores, nudge policy, system prompts, and session lifecycle. Frontends supply
  ///     exactly three decisions via AgentHostOptions. Registration order and lifetimes
  ///     mirror the CLI composition root this replaces.
  ///     <paramref name="database"/> lets multi-session hosts share ONE app database;
  ///     when omitted each container constructs its own (single-session hosts).
  ///     <paramref name="overrides"/> carries the remaining optional per-container knobs.</summary>
  /// <exception cref="ArgumentException">providerName is not a known provider id.</exception>
  /// <exception cref="InvalidOperationException">the selected provider has no configured API key.</exception>
  public static IServiceCollection AddEThangAgentCore(this IServiceCollection services,
      AgentSettings settings, string providerName, ModelConfig defaultModel, AgentHostOptions host,
      AppDatabase? database = null, CompositionOverrides? overrides = null)
  {
    CompositionOverrides knobs = overrides ?? new CompositionOverrides();
    IEnumerable<Message>? conversationSeed = knobs.ConversationSeed;
    ProcessMailboxLocator? mailboxLocator = knobs.MailboxLocator;
    IComputerAccessProvider? computerAccessProvider = knobs.ComputerAccessProvider;
    ArgumentNullException.ThrowIfNull(settings);
    ArgumentNullException.ThrowIfNull(defaultModel);
    ArgumentNullException.ThrowIfNull(host);
    if (!Providers.IsKnown(providerName))
    {
      throw new ArgumentException(
          $"Unknown provider '{providerName}'. Known providers: {Providers.OpenRouter}.",
          nameof(providerName));
    }

    // Registration ORDER is the contract: each group below appends its registrations in
    // the exact sequence the single chain used, so DI resolution order and lifetimes stay
    // identical to the one-chain form this replaced.
    IServiceCollection wired = AddPlatformServices(services, settings, providerName, defaultModel, conversationSeed);
    wired = AddAgentTools(wired, settings);
    wired = AddPersistenceServices(wired, host, database, providerName);
    wired = AddSkillServices(wired, settings);
    wired = AddWakeAndMailboxServices(wired, mailboxLocator);
    wired = AddModelServices(wired, providerName);
    wired = AddSubAgentServices(wired, settings, providerName);
    wired = AddCapabilityServices(wired);
    wired = AddCapabilityRegistry(wired);
    wired = AddExecAndLoopRegistry(wired, settings);
    wired = AddSystemPrompts(wired, host);
    wired = AddRootAgentServices(wired, settings, providerName, defaultModel, computerAccessProvider);
    wired = AddVerificationServices(wired, settings);

    // Runtime selection (R3.4): in-process by default; RemoteHost = true routes child
    // starts through the out-of-process ChildHost under RemoteHostSupervisor.
    if (settings.RemoteHost)
    {
      wired = AddRemoteHostServices(wired, settings);
    }

    return wired;
  }

  /// <summary>The selected provider's transport, the conversation, and the platform ACLs
  ///     (file system, file search, git, worktrees, exec, web, skill registry, documents).
  ///     Registered first: every later group resolves these seams.</summary>
  private static IServiceCollection AddPlatformServices(IServiceCollection services, AgentSettings settings,
      string providerName, ModelConfig defaultModel, IEnumerable<Message>? conversationSeed)
  {
    IServiceCollection wired = AddProviderServices(services, settings, providerName)
        .AddSingleton(defaultModel)
        // Seed hydrates a resumed session's transcript; a fresh session passes null
        // and starts from an empty conversation.
        .AddSingleton(_ => new Conversation(conversationSeed))
        .AddSingleton<IConversationRepository, InMemoryConversationRepository>()
        .AddSingleton<DirectFileSystemAccess>()
        .AddSingleton<IFileSystemAccess>(sp => sp.GetRequiredService<DirectFileSystemAccess>())
        .AddSingleton<IFileSearchAccess, DirectFileSearchAccess>()
        .AddSingleton<IFileWriteAccess>(sp => sp.GetRequiredService<DirectFileSystemAccess>())
        .AddSingleton<IFileEditAccess>(sp => sp.GetRequiredService<DirectFileSystemAccess>())
        .AddSingleton<DirectGitAccess>()
        .AddSingleton<IGitQueryAccess>(sp => sp.GetRequiredService<DirectGitAccess>())
        .AddSingleton<IGitCommitAccess>(sp => sp.GetRequiredService<DirectGitAccess>())
        .AddSingleton<GitWorktreeAccess>()
        .AddSingleton<IGitWorktreeAccess>(sp => sp.GetRequiredService<GitWorktreeAccess>())
        .AddSingleton<IWorktreeProvisioner>(sp => new GitWorktreeProvisioner(sp.GetRequiredService<GitWorktreeAccess>()))
        .AddSingleton<IWorkspaceCleanlinessCheck>(sp => new WorkspaceCleanlinessCheck(sp.GetRequiredService<IGitQueryAccess>()))
        .AddSingleton(ExecOptions.Default)
        .AddSingleton<IExecActivitySink>(_ => NullExecActivitySink.Instance)
        .AddSingleton<IWebAccess, HttpWebAccess>()
        .AddSingleton<ISkillRegistryAccess, GitSkillRegistryAccess>()
        .AddSingleton<ISkillsShAccess, HttpSkillsShAccess>()
        .AddSingleton<IHtmlToMarkdown, HtmlAgilityMarkdownConverter>()
        .AddSingleton<IDocxAccess, DirectDocxAccess>()
        .AddSingleton<IPdfAccess, DirectPdfAccess>();
    return wired;
  }

  /// <summary>The agent's tool surface: every built-in tool binding plus the
  ///     settings-gated computer tool.</summary>
  private static IServiceCollection AddAgentTools(IServiceCollection wired, AgentSettings settings) =>
      wired.AddSingleton(sp => new AgentToolsProvider("agent", AgentToolBindings(sp, settings)));

  /// <summary>The full binding list in registration order: file and document tools, then
  ///     database and skill tools, context and git tools, workspace tools, and the
  ///     settings-gated computer tool.</summary>
  private static IReadOnlyList<AgentToolBinding> AgentToolBindings(IServiceProvider sp, AgentSettings settings) =>
  [
      .. FileToolBindings(sp),
      .. DataToolBindings(sp),
      .. ContextAndGitToolBindings(sp),
      .. WorkspaceToolBindings(sp),
      .. ComputerToolBindings(sp, settings),
  ];

  /// <summary>File and document tool bindings: read, image, docx, pdf, write,
  ///     write-markdown, edit, and file-name search.</summary>
  private static IReadOnlyList<AgentToolBinding> FileToolBindings(IServiceProvider sp) =>
  [
      new AgentToolBinding(
          new ReadTool(sp.GetRequiredService<IPathResolver>(),
              sp.GetRequiredService<IFileSystemAccess>()),
          "Read lines from a text file."),
      new AgentToolBinding(
          new ReadImageTool(sp.GetRequiredService<IPathResolver>(),
              sp.GetRequiredService<IFileSystemAccess>(),
              new SessionImageInputCapability(sp)),
          "Read one image file from the workspace and attach it to the result."),
      new AgentToolBinding(
          new DocxTool(sp.GetRequiredService<IPathResolver>(),
              sp.GetRequiredService<IDocxAccess>(),
              new SessionImageInputCapability(sp)),
          "Read, edit, create, and inspect .docx documents."),
      new AgentToolBinding(
          new PdfTool(sp.GetRequiredService<IPathResolver>(),
              sp.GetRequiredService<IPdfAccess>(),
              new SessionImageInputCapability(sp)),
          "Read, render, create, and manipulate PDF documents."),
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
          new FindFilesTool(sp.GetRequiredService<IPathResolver>(),
              sp.GetRequiredService<IFileSearchAccess>()),
          "Search the workspace for files by name pattern (Win32 glob: * and ?)."),
  ];

  /// <summary>Database and skill tool bindings: the agent's own database, workspace
  ///     SQLite files, the skill catalog tools, and skill invocation.</summary>
  private static IReadOnlyList<AgentToolBinding> DataToolBindings(IServiceProvider sp) =>
  [
      new AgentToolBinding(
          new DbSchemaTool(sp.GetRequiredService<ISelfDatabaseAccess>()),
          "List the tables, columns, and indexes of the agent's own app database."),
      new AgentToolBinding(
          new DbQueryTool(sp.GetRequiredService<ISelfDatabaseAccess>()),
          "Run one read-only SQL query against the agent's own app database."),
      new AgentToolBinding(
          new SqliteQueryTool(sp.GetRequiredService<IPathResolver>(),
              sp.GetRequiredService<ISqliteFileAccess>()),
          "Run one read-only SQL query against any SQLite file in the workspace."),
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
          sp.GetRequiredService<SkillSearchTool>(),
          "Search the skills.sh directory for community skills."),
      new AgentToolBinding(
          sp.GetRequiredService<SkillRegistryTool>(),
          "Install, update, or uninstall community skills."),
      new AgentToolBinding(
          sp.GetRequiredService<SkillInvokeTool>(),
          "Invoke a named catalog skill on the user's behalf (including manual skills)."),
  ];

  /// <summary>Context and git tool bindings: the workspace task list, the experiment
  ///     ledger, context edit and trim, git status, diff, commit, and worktrees.</summary>
  private static IReadOnlyList<AgentToolBinding> ContextAndGitToolBindings(IServiceProvider sp) =>
  [
      new AgentToolBinding(
          new TodoTool(new StateServiceTodoListStore(sp.GetRequiredService<IStateService>())),
          "Track a workspace task list."),
      new AgentToolBinding(
          new ExperimentTool(sp.GetRequiredService<IExperimentLedgerStore>()),
          "Record the scientific loop's hypotheses, predictions, verdicts, observations, conclusions, and skips."),
      new AgentToolBinding(
          new ContextEditTool(sp.GetRequiredService<IConversationContextService>()),
          "Edit this conversation's own context (list, remove, shorten messages)."),
      new AgentToolBinding(
          new ContextTrimTool(sp.GetRequiredService<IConversationContextService>()),
          "Trim tool results this conversation no longer needs (marker-preserving cleanup)."),
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
              sp.GetRequiredService<ICommitStyleProvider>(),
              sp.GetRequiredService<VerificationGate>()),
          "Commit the current index with a validated conventional or gitmoji message."),
      new AgentToolBinding(
          new WorktreeTool(sp.GetRequiredService<IPathResolver>(),
              sp.GetRequiredService<IGitWorktreeAccess>()),
          "List git worktrees, or create/remove one under .worktrees."),
  ];

  /// <summary>Workspace tool bindings: user-command read-back, web fetch, the OpenRouter
  ///     management surface, the pure-graph cycle check, and MCP dispatch.</summary>
  private static IReadOnlyList<AgentToolBinding> WorkspaceToolBindings(IServiceProvider sp) =>
  [
      new AgentToolBinding(
          new CommandOutputTool(sp.GetRequiredService<ICommandRunStore>()),
          "Read the stored output of a user command run (a ! command the user ran in chat)."),
      new AgentToolBinding(
          new WebFetchTool(sp.GetRequiredService<IWebAccess>(),
              sp.GetRequiredService<IHtmlToMarkdown>()),
          "Fetch a web page or resource over HTTP(S) and return readable text (HTML converted to markdown; other text verbatim)."),
      new AgentToolBinding(
          new OpenRouterManagementTool(sp.GetRequiredService<IOpenRouterManagementAccess>()),
          "Manage OpenRouter provisioning keys through the OpenRouter Management API (list, create, get, update, delete, current)."),

      // Pure graph math, no external access: safe for sub-agents too.
      new AgentToolBinding(
          new CycleCheckTool(),
          "Detect dependency cycles in a supplied construction graph and classify deadlock risk."),
      // MCP dispatch (issue #104): the model's single door to configured MCP
      // servers; the definition budget stays flat (B1). The SHARED access
      // (issue #106): the loop tool and the Desktop status view see the same
      // pooled sessions.
      new AgentToolBinding(
          new McpTool(sp.GetRequiredService<IMcpServerAccess>(), new AmbientMcpGrantScopeAdapter()),
          "Call tools on configured MCP servers through one dispatch surface (list, call)."),
  ];

  /// <summary>Host-supplied workspace identity, the app database, the storage-backed stores, the
  ///     context-window and compaction plumbing, and the shell and user-command surface.
  /// </summary>
  private static IServiceCollection AddPersistenceServices(IServiceCollection wired, AgentHostOptions host,
      AppDatabase? database, string providerName)
  {
    return wired
        .AddSingleton(host.WorkspaceContext)
        .AddSingleton(host.PathResolver)
        // One app-owned database: hosts opening several sessions pass a shared
        // instance here so every session's stores hit the same SQLite file.
        .AddSingleton(_ => database ?? new AppDatabase())
        // The provider model catalog persists between app runs (stale-while-revalidate):
        // the model picker and smart-selection previews serve the last fetched catalog
        // instantly and refresh in the background. The ACL scopes keys by provider plus
        // API source, so configured proxies and mock servers cannot poison the live catalog.
        .AddSingleton<IModelCatalogCache>(sp => new SqliteModelCatalogCache(
            sp.GetRequiredService<AppDatabase>()))
        .AddSingleton<IAppPreferenceStore>(sp => new SqliteAppPreferenceStore(
            sp.GetRequiredService<AppDatabase>()))
        .AddSingleton<ISelfDatabaseAccess, SqliteSelfDatabaseAccess>()
        .AddSingleton<ISqliteFileAccess, SqliteFileAccess>()
        // MCP dispatch (issue #104): the config store over the app database, the SDK
        // pool, and the per-workspace access provider. The mcp loop tool below is the
        // model's single door; the definition budget stays flat (B1).
        .AddSingleton<IMcpServerStore>(sp => new SqliteMcpServerStore(
            sp.GetRequiredService<AppDatabase>()))
        .AddSingleton<IMcpClientSessionPool, SdkMcpClientSessionFactory>()
        .AddSingleton<IMcpServerAccessProvider, McpServerAccessProvider>()
        // Issue #106: ONE access per container - the mcp tool's dispatches and the
        // Desktop status view share this workspace's pooled sessions.
        .AddSingleton(SharedMcpServerAccess)
        .AddSingleton<IContextWindowSource, CatalogContextWindowSource>()
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
        .AddSingleton<DirectShellAccess>()
        .AddSingleton<IShellCommandAccess>(sp => sp.GetRequiredService<DirectShellAccess>())
        .AddSingleton<ICommandRunStore>(sp => new SqliteCommandRunStore(
            sp.GetRequiredService<AppDatabase>(),
            sp.GetRequiredService<IWorkspaceContext>().WorkspaceId))

        .AddSingleton(sp => new UserCommandRunner(
            sp.GetRequiredService<IWorkspaceContext>().WorkspaceId,
            sp.GetRequiredService<IShellCommandAccess>(),
            sp.GetRequiredService<ICommandRunStore>(),
            sp.GetRequiredService<Conversation>()))
        .AddSingleton<IUserCommandRunner>(sp => sp.GetRequiredService<UserCommandRunner>())
        .AddSingleton<IStateStore, SqliteStateStore>()
        .AddSingleton<IPlanStore>(sp => new SqlitePlanStore(
            sp.GetRequiredService<AppDatabase>(),
            sp.GetRequiredService<IWorkspaceContext>().WorkspaceId))
        .AddSingleton<PlanService>()
        .AddSingleton<IPlanTodoCleaner>(sp => new StateServicePlanTodoCleaner(sp.GetRequiredService<IStateService>()))
        // The experiment ledger rides state keys (append-only), exactly as the todo tool
        // rides todo/list: the tool (a later task) resolves this port, never IStateService.
        .AddSingleton<IExperimentLedgerStore>(sp => new StateServiceExperimentLedgerStore(sp.GetRequiredService<IStateService>()))
        .AddSingleton<IAgentStore, SqliteAgentStore>()
        .AddSingleton<IAgentHeartbeat>(_ => new InMemoryAgentHeartbeat(TimeProvider.System))
        .AddSingleton<IWatchdogEventStore>(sp => new SqliteWatchdogEventStore(
            sp.GetRequiredService<AppDatabase>()));
  }

  /// <summary>The composite skill catalog, its hot-reload watcher, the registry service and tools,
  ///     and the shared skill-invocation core.
  /// </summary>
  private static IServiceCollection AddSkillServices(IServiceCollection wired, AgentSettings settings)
  {
    return wired
        // Composite skill catalog (skill-routing Phase 1, Task 7): built-ins plus
        // file skills loaded through the directory-source ACL from the resolved
        // skill-directory configuration. When nothing is configured the composite
        // is behavior-identical to the bare embedded catalog it replaced. The
        // concrete registration stays so anything resolving the embedded catalog
        // directly keeps doing so; the skill tools and the bootstrap prompt
        // provider resolve ISkillCatalog and see the merged view.
        .AddSingleton<EmbeddedSkillCatalog>()
        .AddSingleton<ISkillDirectorySource, DirectorySkillSource>()
        .AddSingleton(sp => new CompositeSkillCatalog(
            sp.GetRequiredService<EmbeddedSkillCatalog>(),
            sp.GetRequiredService<ISkillDirectorySource>(),
            sp.GetRequiredService<ResolvedSkillDirectories>().List))
        .AddSingleton<ISkillCatalog>(sp => sp.GetRequiredService<CompositeSkillCatalog>())
        // Hot reload (spec #26): ONE watcher + reloader per container. The factory
        // starts the watcher after the session exists; child containers never do,
        // so their watchers stay inert (named limitation in the spec).
        .AddSingleton(sp => new SkillDirectoryReloader(
            () => sp.GetRequiredService<Conversation>(),
            notice => sp.GetRequiredService<AgentSession>().PostNotice(notice)))
        .AddSingleton(sp => new SkillDirectoryWatcher(
            sp.GetRequiredService<CompositeSkillCatalog>(),
            diff => sp.GetRequiredService<SkillDirectoryReloader>().Announce(diff),
            sp.GetRequiredService<ResolvedSkillDirectories>().List))
        // Skill registry (plan #29): one service per container; the resolvers
        // read the same settings and resolved-directories the watcher uses.
        .AddSingleton(sp => CreateSkillRegistryService(sp, settings))
        .AddSingleton(sp => new SkillSearchTool(sp.GetRequiredService<SkillRegistryService>()))
        .AddSingleton(sp => new SkillRegistryTool(sp.GetRequiredService<SkillRegistryService>()))
        // Skill invocation channel (plan #30): ONE shared core — the Desktop's
        // slash input and the skill_invoke tool resolve through the same
        // SkillInvocationService; the tool reaches it through the port and
        // appends out-of-turn through the conversation sink (both seams live in
        // the Tool Domain; Conversation Domain references Tool Domain, never the
        // reverse).
        .AddSingleton<SkillInvocationService>()
        .AddSingleton<ISkillInvocationPort>(sp => new SkillInvocationPortAdapter(
            sp.GetRequiredService<SkillInvocationService>()))
        .AddSingleton<IConversationSink>(sp => new ConversationSinkAdapter(
            () => sp.GetRequiredService<Conversation>()))
        .AddSingleton(sp => new SkillInvokeTool(
            sp.GetRequiredService<ISkillInvocationPort>(),
            sp.GetRequiredService<IConversationSink>(),
            notice => sp.GetRequiredService<AgentSession>().PostNotice(notice)))
        .AddSingleton<ILearnedSkillStore, SqliteLearnedSkillStore>()
        .AddSingleton<Func<DateTimeOffset>>(_ => () => DateTimeOffset.UtcNow)
        .AddSingleton<SqliteCuratedMemoryStore>()
        .AddSingleton<ICuratedMemoryStore>(sp => sp.GetRequiredService<SqliteCuratedMemoryStore>());
  }

  /// <summary>The wait wake bus and its adapters, the root inbox, the mailbox stores, the supervisor
  ///     and mailbox registries, and the nudge policy.
  /// </summary>
  private static IServiceCollection AddWakeAndMailboxServices(IServiceCollection wired, ProcessMailboxLocator? mailboxLocator)
  {
    return wired
        // The wait wake bus (spec #62): one hub per session; the child-event adapter and
        // the state-change adapter feed it; the root inbox decorator fires the
        // unconditional user wake. Ephemeral, in-process — remote waits keep today's
        // behavior (documented at RemoteAgentRuntime).
        // The adapters are constructed here explicitly: DI constructs lazily, and NOTHING
        // else resolves the adapters — a plain registration would never subscribe. The hub
        // factory builds the hub FIRST, then the adapters over it (no re-entrance). The
        // adapters' leases live in the streams' subscription closures and die with the
        // session-scoped streams; the adapters are never disposed individually (benign:
        // InProcessAgentEvents and InProcessStateChangeBus are session-scoped).
        .AddSingleton(sp =>
        {
          WaitWakeHub hub = new();
          _ = new ChildEventWakeAdapter(sp.GetRequiredService<IAgentEvents>(), hub);
          _ = new StateChangeWakeAdapter(sp.GetRequiredService<IStateChangeBus>(), hub);
          return hub;
        })
        .AddSingleton<IStateChangeBus, InProcessStateChangeBus>()
        .AddSingleton<IAgentInbox>(sp => new SignalingInbox(
            new BoundedAgentMailbox(),
            sp.GetRequiredService<WaitWakeHub>(),
            ownerId: sp.GetRequiredService<RootSessionIdentity>().Id ?? AgentId.NewId()))
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
        .AddSingleton<INudgePolicy>(_ => new DefaultNudgePolicy());
  }

  /// <summary>The sub-agent stack: the workspace anchor scope, the spawner and its services, the
  ///     runtime, and the spawn command.
  /// </summary>
  private static IServiceCollection AddSubAgentServices(IServiceCollection wired, AgentSettings settings, string providerName)
  {
    return wired
        // The workspace anchor scope (worktree ladder, T7): one instance per session
        // container, written by the spawner around an anchored child's run and read
        // by the exec engine's workspace resolver. AsyncLocal-backed, so the anchor
        // flows down a child's async flow and never leaks into concurrent runs.
        .AddSingleton<IWorkspaceAnchorScope, SessionWorkspaceAnchorScope>()
        .AddSingleton(sp => new SubAgentServices(
            sp.GetRequiredService<IModelProviderFactory>(),
            sp.GetRequiredService<IAgentStore>(),
            sp.GetRequiredService<IToolRegistry>(),
            sp.GetRequiredService<ISystemPromptProvider>(),
            sp.GetRequiredService<SubAgentOptions>(),
            sp.GetRequiredService<IAgentHeartbeat>(),
            sp.GetRequiredService<IAgentEvents>(),
            sp.GetRequiredService<IWatchdogEventStore>(),
            InboxFor: id => sp.GetRequiredService<ChildMailboxRegistry>().InboxFor(id),
            AnchorScope: sp.GetRequiredService<IWorkspaceAnchorScope>(),
            Cleanliness: sp.GetRequiredService<IWorkspaceCleanlinessCheck>(),
            SupervisorFor: id => sp.GetRequiredService<ChildSupervisorRegistry>().Find(id),
            // Spawn-boundary invariant (issue #124): production wiring demands a provider
            // binding on every begin-run — an unbound record fails loudly (Failed + audit
            // row) instead of looping without model calls. The spawn stamp above always
            // supplies one; this is the second net under it.
            ProviderRequired: true))
        .AddSingleton(sp => new SubAgentSpawner(
            sp.GetRequiredService<SubAgentServices>(),
            sp.GetRequiredService<SessionModelPreferences>(),
            sp.GetRequiredService<IContextWindowSource>(),
            sp.GetRequiredService<DefaultContextCompactor>(),
            sp.GetRequiredService<IModelCatalog>()))
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
                ChildToolSurface: ChildToolSurface(sp),
                WorkspaceRoot: sp.GetRequiredService<IWorkspaceContext>().WorkspaceId,
                ProviderName: providerName),
            sp.GetRequiredService<IContextWindowSource>(),
            sp.GetRequiredService<IWorktreeProvisioner>(),
            sp.GetRequiredService<IModelCatalog>()));
  }

  /// <summary>The query handlers, the link registry, the spawn-graph handler, and the capability
  ///     providers every agent surface shares.
  /// </summary>
  private static IServiceCollection AddCapabilityServices(IServiceCollection wired)
  {
    return wired
        .AddSingleton<IAgentQueries, AgentQueries>()
        .AddSingleton<IMemoryRecallQuery, RecallQueryHandler>()
        .AddSingleton<IMemorySessionsQuery, SessionsQueryHandler>()
        .AddSingleton(sp => new AgentLinkRegistry(
            new SqliteLinkStore(sp.GetRequiredService<AppDatabase>()),
            () => sp.GetRequiredService<IWorkspaceContext>().WorkspaceId))
        .AddSingleton<SpawnGraphHandler>()
        .AddSingleton(sp =>
        {
          SpawnGraphHandler graph = sp.GetRequiredService<SpawnGraphHandler>();
          return new AgentCapabilityProvider(new AgentProviderServices(
                  sp.GetRequiredService<IAgentSpawnCommand>(),
                  sp.GetRequiredService<IAgentQueries>(),
                  // The parent-context fallback (root spawning a child: RunningChild is
                  // null there) must carry the PERSISTED root session id — a fresh random
                  // id here phantom-parents every root-spawned child to a row that has
                  // none (2026-10-08 Resumes session: 43 children pointed at an id no row
                  // ever held; grandchildren parented correctly because RunningChild
                  // carries the real child id). Resolved lazily per dispatch, exactly as
                  // RootAgentHolder resolves the agent's request identity.
                  () => SubAgentSpawner.RunningChild ?? RootParentRecord(sp),
                  sp.GetRequiredService<IAgentRuntime>(),
                  sp.GetRequiredService<AgentLinkRegistry>(),
                  Locator: sp.GetRequiredService<ProcessMailboxLocator>(),
                  EventsFor: id => sp.GetRequiredService<ProcessMailboxLocator>().EventsFor(id),
                  WakeSource: sp.GetRequiredService<WaitWakeHub>(),
                  Clock: TimeProvider.System,
                  // Fail-fast join: agent.fanout's advertised contract - failed STARTS
                  // (DepthExceeded, InvalidSpawnRequest, ...) fail the join immediately
                  // and surface under their own error code.
                  Fanout: async (parent, children, ct) =>
                  {
                    Result<SpawnGraphOutcome> joined = await graph.ExecuteAsync(parent,
                        new SpawnGraphRequest(Label: "", Children: children, Join: new JoinPolicy(true)), ct).ConfigureAwait(false);
                    return joined.IsSuccess
                        ? joined.Value.Render()
                        : "Error [" + joined.Error.Code + "]: " + joined.Error.Message;
                  }));
        })
        .AddSingleton(_ => EvidenceOptions.Default)
        .AddSingleton<IEvidenceRunner, CSharpEvidenceRunner>()
        .AddSingleton<IStateService, StateService>()
        .AddSingleton<StateCapabilityProvider>()
        .AddSingleton(sp => new PlanCapabilityProvider(
            sp.GetRequiredService<PlanService>(),
            () => sp.GetRequiredService<RootSessionIdentity>().Id?.ToString()))
        .AddSingleton<MemoryCapabilityProvider>()
        .AddSingleton(sp => new CuratedMemoryCapabilityProvider(
            sp.GetRequiredService<ICuratedMemoryStore>(),
            () => sp.GetRequiredService<IWorkspaceContext>().WorkspaceId,
            () => SubAgentSpawner.RunningChild?.Id.ToString(),
            sp.GetRequiredService<SessionMemoryWriteCounter>().Increment,
            () => DateTimeOffset.UtcNow));
  }

  /// <summary>The capability registry and its deferred factory - the deadlock-safe lazy resolution
  ///     the registration comments describe.
  /// </summary>
  private static IServiceCollection AddCapabilityRegistry(IServiceCollection wired)
  {
    return wired
        .AddSingleton<ICapabilityRegistry>(sp =>
            CapabilityRegistry.Create(AgentSurface(sp, sp.GetRequiredService<AgentToolsProvider>())))
        // MUST stay lazy inside this closure: the agent surface reaches back to
        // IExecEngine (agent -> spawn -> tool registry -> exec tool), so building any
        // registry eagerly here would re-enter this not-yet-finished singleton and
        // park forever on the container's in-progress slot (TLC-proven deadlock,
        // DiResolution.tla). Deferred like the Lazy<> wiring this replaced.
        .AddSingleton<Func<ICapabilityRegistry>>(sp =>
        {
          Lazy<ICapabilityRegistry> root = new(() => CapabilityRegistry.Create(
                  AgentSurface(sp, sp.GetRequiredService<AgentToolsProvider>())));
          AgentToolsProvider tools = sp.GetRequiredService<AgentToolsProvider>();
          ConcurrentDictionary<string, ICapabilityRegistry> anchored = new(StringComparer.Ordinal);

          // Dispatch-time resolution (R1 + capability-surface re-rooting): the ambient
          // running child's contract decides the surface. An anchored contract serves the
          // memoized ANCHORED provider registry - every IWorkspaceScopedTool re-rooted at
          // the anchor, advertisement untouched - so the child's exec scripts reach
          // read/write/edit/git AT THE ANCHOR. Grants then wrap outermost:
          // Filtered(Anchored(session)), the loop registry's documented order.
          ICapabilityRegistry ResolveSurface()
          {
            AgentRecord? running = SubAgentSpawner.RunningChild;
            if (running is null)
            {
              return root.Value;
            }

            // Decode once: the same contract feeds the anchor branch and the grant filter.
            SpawnContract contract = running.Contract is { } contractJson
                ? SpawnContract.Decode(contractJson)
                : new SpawnContract();

            ICapabilityRegistry surface = contract.WorkspaceRoot is { } anchor
                ? anchored.GetOrAdd(anchor, a => CapabilityRegistry.Create(AgentSurface(sp, tools.RootedAt(a))))
                : root.Value;

            if (contract.DecodedEffectiveTools is { } effective)
            {
              AgentId childId = running.Id;
              surface = new FilteredCapabilityRegistry(surface, effective,
                  onDenial: name => AuditGrantDenial(sp, childId, name));
            }

            return surface;
          }

          return () => ResolveSurface();
        });
  }

  /// <summary>The exec engine, the exec tool, and the loop's tool registry.
  /// </summary>
  private static IServiceCollection AddExecAndLoopRegistry(IServiceCollection wired, AgentSettings settings)
  {
    return wired
        .AddSingleton<IExecEngine>(sp => new CSharpScriptExecEngine(
            sp.GetRequiredService<Func<ICapabilityRegistry>>(),
            // Registry and workspace are both resolved per execution so concurrent
            // sessions in one process each see their own context, never a stale
            // construction-time value pinned to whichever container was built first.
            // An anchored child run lifts the workspace anchor scope around its loop
            // (T7), so its exec scripts resolve Workspace at the CHILD's anchor; the
            // session workspace is the fallback whenever no anchored run is ambient.
            () => sp.GetRequiredService<IWorkspaceAnchorScope>().Current
                ?? sp.GetRequiredService<IWorkspaceContext>().WorkspaceId,
            // Verification sink (spec #24): every completed Shell run lands in the
            // session's ledger; resolved per execution like the workspace.
            () => sp.GetRequiredService<IVerificationLedger>()))
        .AddSingleton<ITool>(sp => new ExecTool(
            sp.GetRequiredService<IExecEngine>(),
            sp.GetRequiredService<ExecOptions>(),
            sp.GetRequiredService<IExecActivitySink>()))
        .AddSingleton<IToolRegistry>(sp =>
            new ToolRegistry([sp.GetRequiredService<ITool>(),
                new McpTool(sp.GetRequiredService<IMcpServerAccess>(), new AmbientMcpGrantScopeAdapter()),
                new OpenRouterManagementTool(sp.GetRequiredService<IOpenRouterManagementAccess>()),
                new ReadImageTool(sp.GetRequiredService<IPathResolver>(),
                    sp.GetRequiredService<IFileSystemAccess>(),
                    new SessionImageInputCapability(sp)),
                new DocxTool(sp.GetRequiredService<IPathResolver>(),
                    sp.GetRequiredService<IDocxAccess>(),
                    new SessionImageInputCapability(sp)),
                new PdfTool(sp.GetRequiredService<IPathResolver>(),
                    sp.GetRequiredService<IPdfAccess>(),
                    new SessionImageInputCapability(sp)),
                .. ComputerLoopTools(sp, settings)]));
  }

  /// <summary>The composite system prompt: skills bootstrap and listing, the static identity, the
  ///     exec guide, the memory guide, context hygiene, and any frontend-appended providers.
  /// </summary>
  private static IServiceCollection AddSystemPrompts(IServiceCollection wired, AgentHostOptions host)
  {
    return wired
        .AddSingleton<ISystemPromptProvider>(sp => new CompositeSystemPromptProvider(
        [
            new SkillsBootstrapPromptProvider(sp.GetRequiredService<ISkillCatalog>(),
                sp.GetRequiredService<ICommitStyleProvider>()),
                new SkillsListingPromptProvider(sp.GetRequiredService<ISkillCatalog>(),
                    sp.GetRequiredService<ILearnedSkillStore>(),
                    sp.GetRequiredService<ResolvedSkillDirectories>().List),
                new StaticPromptProvider(
                    "You are eThang Agent, an AI coding agent for Windows. Work in the current " +
                    "workspace, prefer the provided tools over guessing, and keep responses tight."),
                new ExecGuidePromptProvider(
                    new Lazy<ICapabilityRegistry>(() => sp.GetRequiredService<ICapabilityRegistry>())),
                new CuratedMemoryGuidePromptProvider(),
                new ContextHygienePromptProvider(),
                ..host.ExtraPromptProviders,
        ]));
  }

  /// <summary>The root agent's collaborators: child options, the image capability, the computer
  ///     access provider, model preferences, the lazy root holder and resolver, and the root turn handler.
  /// </summary>
  private static IServiceCollection AddRootAgentServices(IServiceCollection wired, AgentSettings settings,
      string providerName, ModelConfig defaultModel, IComputerAccessProvider? computerAccessProvider)
  {
    return wired
        .AddSingleton(subAgents(settings, defaultModel.ModelId))
        // Root agent is built lazily on the first turn (and rebuilt on every model
        // change) by RootAgentHolder; the root is NOT known at container build time.
        // The holder reuses the shared
        // Conversation/provider/tools/system-prompt so a rebuild preserves all message history.
        .AddSingleton<SessionImageInputCapability>()
        // F4 (fix round 5): the registry + provider are PROCESS-level. A host either
        // supplies one (the session factory mints it once) or the first container
        // builds it - but never one per session container, or each session would own
        // its own ObservationLedger/FrameRegistry/lease over one broker connection.
        .AddSingleton(sp => computerAccessProvider ?? new BrokerComputerAccessProvider(
            new BrokerRegistry(
                () => Path.Combine(AppContext.BaseDirectory, "eThangAgent.ComputerUse.Host.exe"),
                BrokerRegistry.DefaultPipeNameFor)))
        .AddSingleton<SessionModelPreferences>()
        .AddSingleton<RootSessionIdentity>()
        .AddSingleton(sp => new RootAgentHolder(
            sp.GetRequiredService<IModelProvider>(),
            sp.GetRequiredService<Conversation>(),
            sp.GetRequiredService<IToolRegistry>(),
            dependencies: new RootDependencies(
                SystemPrompt: sp.GetRequiredService<ISystemPromptProvider>(),
                ContextCompactor: sp.GetRequiredService<DefaultContextCompactor>()),
            // The persisted root session id keys OpenRouter sticky sessions (prompt
            // caching): read lazily — the factory sets RootSessionIdentity AFTER the
            // container builds, so a raw value here would always be null.
            sessionIdSource: () => sp.GetRequiredService<RootSessionIdentity>().Id?.ToString()))
        .AddSingleton(sp => new RootAgentResolver(
            new RootModelContext(
                sp.GetRequiredService<IAgentStore>(),
                sp.GetRequiredService<RootSessionIdentity>(),
                Providers.FallbackModelId(providerName),
                defaultModel.MaxTokens,
                defaultModel.Temperature,
                sp.GetRequiredService<IContextWindowSource>(),
                sp.GetRequiredService<IModelCatalog>()),
            sp.GetRequiredService<SessionModelPreferences>()))
        .AddSingleton(sp => new SendMessageCommandHandler(
            agent: null,
            sp.GetRequiredService<Conversation>(),
            sp.GetRequiredService<INudgePolicy>(),
            () => sp.GetRequiredService<SessionMemoryWriteCounter>().Count,
            sp.GetRequiredService<IAgentInbox>(),
            rootTurn: new RootTurnServices(
                sp.GetRequiredService<RootAgentHolder>(),
                sp.GetRequiredService<RootAgentResolver>()),
            verification: new VerificationCollaborators(
                sp.GetRequiredService<VerificationGate>(),
                ChangedFilesResolver(sp))))
        .AddSingleton<IConversationContextService>(sp => new ConversationContextServiceAdapter(sp.GetRequiredService<Conversation>()))
        .AddSingleton<RootSessionLifecycle>();
  }

  /// <summary>The verification ledger, its specifications and gate, and the skill-directory carriers
  ///     every container resolves.
  /// </summary>
  private static IServiceCollection AddVerificationServices(IServiceCollection wired, AgentSettings settings)
  {
    return wired
        // Skill-directory config rides EVERY container (skill-routing Phase 1): the
        // factory fills the carrier at open/resume, and the RemoteHost block below
        // overlays the values onto the settings JSON - so BOTH containers (the app's
        // and the remote ChildHost's) resolve the same config shape from
        // AgentSettings. Unconditional registration: not remote resolves nulls, the
        // empty directory list, and nothing else changes.
        .AddSingleton<IVerificationLedger, SessionVerificationLedger>()
        .AddSingleton(sp => new VerificationCommandSpecification(
            VerificationCommandSpecificationDefaults.Commands))
        .AddSingleton<VerificationFreshnessSpecification>()
        .AddSingleton(sp => new VerificationGate(
            sp.GetRequiredService<IVerificationLedger>(),
            sp.GetRequiredService<VerificationFreshnessSpecification>(),
            settings.VerificationGateEnabled))
        .AddSingleton<SkillDirectoriesCarrier>()
        .AddSingleton(_ => new ResolvedSkillDirectories(ResolveSkillDirectoriesFromSettings(settings)));
  }

  /// <summary>Runtime selection (R3.4): the out-of-process ChildHost supervisor and the wire runtime
  ///     that replaces the in-process one.
  /// </summary>
  private static IServiceCollection AddRemoteHostServices(IServiceCollection wired, AgentSettings settings)
  {
    return wired
          .AddSingleton<SessionFilesCarrier>()
          .AddSingleton(sp => new RemoteHostSupervisor(
              sp.GetRequiredService<IWorkspaceContext>().WorkspaceId,
              Path.Combine(Path.GetTempPath(), "ethang-agent", RemoteHostSupervisor.ScratchFolderFor(sp.GetRequiredService<IWorkspaceContext>().WorkspaceId)),
              settings.WithSessionFiles(
                  sp.GetRequiredService<SessionFilesCarrier>().Global,
                  sp.GetRequiredService<SessionFilesCarrier>().Workspace)
               .WithSkillDirectories(
                  sp.GetRequiredService<SkillDirectoriesCarrier>().Global,
                  sp.GetRequiredService<SkillDirectoriesCarrier>().Workspace),
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


  /// <summary>Resolves skill directories from the settings-carried stored lists - the
  ///     config shape a host built from serialized AgentSettings (the remote
  ///     ChildHost) resolves through. App-side containers REPLACE the
  ///     core's settings-sourced registration in AgentSessionFactory.BuildContainer,
  ///     where the raw preference values were just read from the store; the host
  ///     replaces it too - SessionHost calls this same resolver (shared composition
  ///     glue, public for the cross-assembly call; ref assemblies drop internals),
  ///     so remote children render the identical listing. The duplicate-path
  ///     rule matches the factory's resolution: parse validates shape only; a workspace
  ///     path equal (case-insensitive, full-path normalized) to an already-included
  ///     global path is skipped with no error - the global entry wins.</summary>
  public static IReadOnlyList<SkillDirectory> ResolveSkillDirectoriesFromSettings(AgentSettings settings)
  {
    ArgumentNullException.ThrowIfNull(settings);

    return AgentSessionFactory.ResolveSkillDirectories(
        settings.SkillDirectoriesGlobal, settings.SkillDirectoriesWorkspace,
        settings.WorkspaceRoot ?? string.Empty);
  }

  /// <summary>Wires the EXCLUSIVELY selected provider's chat transport: configuration,
  ///     a named HttpClient, and the session's single IModelProvider typed client. Only
  ///     one provider is ever registered per container — switching providers means
  ///     opening a session wired for the other one.</summary>
  private static IServiceCollection AddProviderServices(
      IServiceCollection services, AgentSettings settings, string providerName)
  {
    string? apiKey = providerName switch
    {
      Providers.OpenRouter => settings.OpenRouter.ApiKey,
      _ => throw new ArgumentOutOfRangeException(nameof(providerName), providerName, "Unknown provider id.")
    };

    string MissingKey() => throw new InvalidOperationException(
        $"Provider '{providerName}' is selected but its API key is not configured. " +
        "Add the key under Settings (gear icon) before opening a session with it.");

    return providerName switch
    {
      Providers.OpenRouter => services
          .AddSingleton(new OpenRouterConfiguration(apiKey ?? MissingKey(), settings.OpenRouter.BaseUrl,
              ManagementKey: settings.OpenRouter.ManagementKey))
          // The management key rides a LIVE carrier read at dispatch (the frozen
          // snapshot forced a session restart to pick a saved key up, 2026-10-08):
          // seeded from settings at build, pushed live by the host on save.
          .AddSingleton(new OpenRouterManagementKeyCarrier
          {
            Current = settings.OpenRouter.ManagementKey,
          })
          // App attribution (openrouter.ai/docs/app-attribution): every request the
          // shared client sends — model calls and catalog fetches alike — identifies
          // the app. One Apply point covers the root and every remote child host,
          // which builds its container through this same composition.
          .AddHttpClient("OpenRouter", client =>
          {
            client.Timeout = TimeSpan.FromSeconds(120);
            OpenRouterAppAttribution.Apply(client);
          })
          .Services
          // The typed client is a SEPARATE HttpClient instance from the named one —
          // its defaults need the attribution too, or root-agent and selector requests
          // reach OpenRouter unattributed (they showed there as plain "api").
          .AddHttpClient<IModelProvider, OpenRouterModelProvider>(client =>
          {
            client.Timeout = TimeSpan.FromSeconds(120);
            OpenRouterAppAttribution.Apply(client);
          })
          .Services,
      _ => throw new ArgumentOutOfRangeException(nameof(providerName), providerName, "Unknown provider id."),
    };
  }

  /// <summary>Wires the selected provider's model factory and catalog.</summary>
  private static IServiceCollection AddModelServices(IServiceCollection services, string providerName)
  {
    return providerName switch
    {
      Providers.OpenRouter => services
          .AddSingleton<IModelProviderFactory>(sp => new OpenRouterModelProviderFactory(
              sp.GetRequiredService<OpenRouterConfiguration>(),
              sp.GetRequiredService<IHttpClientFactory>().CreateClient("OpenRouter")))
          // Management API client (the 'openrouter_management' tool's seam): the SAME
          // named HttpClient (attribution + timeouts) and the SAME configuration - the
          // management key rides OpenRouterConfiguration.ManagementKey.
          .AddSingleton<IOpenRouterManagementAccess>(sp => new OpenRouterManagementClient(
              sp.GetRequiredService<IHttpClientFactory>().CreateClient("OpenRouter"),
              sp.GetRequiredService<OpenRouterConfiguration>(),
              managementKeySource: () => sp.GetRequiredService<OpenRouterManagementKeyCarrier>().Current))
          .AddSingleton<IModelCatalog>(sp => new OpenRouterCatalogClient(
              sp.GetRequiredService<IHttpClientFactory>().CreateClient("OpenRouter"),
              sp.GetRequiredService<OpenRouterConfiguration>(),
              sp.GetRequiredService<IModelCatalogCache>(),
              Providers.OpenRouter)),
      _ => throw new ArgumentOutOfRangeException(nameof(providerName), providerName, "Unknown provider id."),
    };
  }

  /// <summary>The parent record the spawn path serves when no child is running (the
  ///     ROOT spawning a child): a depth-0 stand-in whose Id is the PERSISTED root
  ///     session id, resolved at dispatch — never a fresh random id, which
  ///     phantom-parents every root-spawned child to a row that has none
  ///     (2026-10-08 regression). Null identity (legacy wiring, headless stubs)
  ///     keeps the generated-id behavior: the record is then a throwaway stand-in
  ///     and children phantom-parent exactly as before the persisted-root fix.
  ///     ModelUsed stamps the container's bootstrap model for parity with the
  ///     persisted root row.</summary>
  private static AgentRecord RootParentRecord(IServiceProvider sp)
  {
    AgentId? persisted = sp.GetRequiredService<RootSessionIdentity>().Id;
    return AgentRecord.Spawned(
        persisted ?? AgentId.NewId(), null, 0,
        sp.GetRequiredService<ModelConfig>().ModelId, null,
        "root session", DateTimeOffset.UtcNow);
  }
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
    AgentToolsProvider childTools = sp.GetRequiredService<AgentToolsProvider>();
    IEnumerable<string> names = childTools.Actions.Select(a => a.Name)
        .Concat(AgentCapabilityProvider.ActionNames)
        .Concat(sp.GetRequiredService<StateCapabilityProvider>().Actions.Select(a => a.Name))
        .Concat(sp.GetRequiredService<PlanCapabilityProvider>().Actions.Select(a => a.Name))
        .Concat(sp.GetRequiredService<MemoryCapabilityProvider>().Actions.Select(a => a.Name))
        .Concat(sp.GetRequiredService<CuratedMemoryCapabilityProvider>().Actions.Select(a => a.Name));
    return [.. names];
  }

  /// <summary>The verification gate's changed-files resolver: git status over the session
  ///     workspace, an empty list when the status query fails. Extracted verbatim from the
  ///     handler registration lambda so the registration reads as data, not plumbing.</summary>
  private static Func<IReadOnlyList<(string Path, DateTimeOffset ModifiedUtc)>> ChangedFilesResolver(IServiceProvider sp) => () =>
  {
    Result<IReadOnlyList<(string Path, DateTimeOffset ModifiedUtc)>> status =
        sp.GetRequiredService<IGitCommitAccess>().StatusAsync(
            sp.GetRequiredService<IWorkspaceContext>().WorkspaceId)
            .GetAwaiter().GetResult();
    return status.IsSuccess ? status.Value : [];
  };

  /// <summary>The 'computer' tool, bound only when the host's settings enable computer use
  ///     (settings.ComputerUse from the computer_use_enabled preference). The access instance is
  ///     the per-workspace BrokerRegistry entry; the vision capability resolves the session's
  ///     CURRENT ModelConfig at call time, so a model-picker change applies from the next turn.</summary>
  /// <summary>The container's ONE MCP server access (issue #106): the mcp tool's
  ///     dispatches and the Desktop status view share this workspace's pooled
  ///     sessions. Extracted so the registration reads as a method group (IDE0001
  ///     flags the inline factory's generic).</summary>
  private static IMcpServerAccess SharedMcpServerAccess(IServiceProvider sp) =>
      sp.GetRequiredService<IMcpServerAccessProvider>().ForWorkspace(
          sp.GetRequiredService<IWorkspaceContext>().WorkspaceId);

  private static IEnumerable<AgentToolBinding> ComputerToolBindings(IServiceProvider sp, AgentSettings settings)
  {
    if (!settings.ComputerUse)
    {
      yield break;
    }

    yield return new AgentToolBinding(
        new ComputerTool(
            sp.GetRequiredService<IComputerAccessProvider>().ForWorkspace(
                sp.GetRequiredService<IWorkspaceContext>().WorkspaceId)!,
            new SessionImageInputCapability(sp)),
        "Control the desktop (computer use): observe, click, type, and paste in apps.");
  }


  /// <summary>The loop-registry computer tools (task 19): the 'computer' tool joins the
  ///     loop's IToolRegistry when enabled, so its tool result (and any screenshot image part)
  ///     enters the conversation as a first-class tool message on the wire.</summary>
  private static IEnumerable<ITool> ComputerLoopTools(IServiceProvider sp, AgentSettings settings)
  {
    if (!settings.ComputerUse)
    {
      yield break;
    }

    yield return new ComputerTool(
        sp.GetRequiredService<IComputerAccessProvider>().ForWorkspace(
            sp.GetRequiredService<IWorkspaceContext>().WorkspaceId)!,
        new SessionImageInputCapability(sp));
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
        sp.GetRequiredService<PlanCapabilityProvider>(),
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
  /// <summary>Skill registry (plan #29): one service per container; the resolvers
  ///     read the same settings and resolved-directories the watcher uses.</summary>
  private static SkillRegistryService CreateSkillRegistryService(IServiceProvider sp, AgentSettings settings) =>
      new(
          sp.GetRequiredService<ISkillRegistryAccess>(),
          sp.GetRequiredService<ISkillsShAccess>(),
          sp.GetRequiredService<ISkillCatalog>(),
          () => settings.SkillRegistryDefaultTarget,
          () => [.. sp.GetRequiredService<ResolvedSkillDirectories>().List.Select(d => d.Path)]);
}
