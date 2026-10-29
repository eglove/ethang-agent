using System.Text.Json;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.AgentInfrastructure;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.ChildHost;

/// <summary>The host's own headless session composition: the provider stack built from the
///     settings JSON the app persists for the host (API keys travel host-side once at startup,
///     never per-envelope), sharing the SAME database file so records and transcripts written
/// </summary>
public sealed class SessionHost
{
  private SessionHost(IServiceProvider services, IAgentStore store, IAgentRuntime runtime,
      InProcessAgentRuntime localRunner, ChildMailboxRegistry mailboxes, WatchdogSettings? watchdog,
      RootRunHost? rootRun)
  {
    Services = services;
    Store = store;
    Runtime = runtime;
    LocalRunner = localRunner;
    Mailboxes = mailboxes;
    Watchdog = watchdog;
    RootRun = rootRun;
  }

  /// <summary>The child container — the host watchdog resolves the same seams the app
  ///     side does (heartbeat, event store, event stream, supervisor registry) from it.</summary>
  public IServiceProvider Services { get; }
  public IAgentStore Store { get; }

  /// <summary>This container's orchestration runtime: it launches a HOST per agent (issue #305
  ///     D1/D2), so a child started through it becomes its own process.</summary>
  public IAgentRuntime Runtime { get; }

  /// <summary>The LEAF EXECUTOR for the one agent this host owns (issue #305 D1). The server runs
  ///     a child through THIS rather than through <see cref="Runtime"/>, because <see cref="Runtime"/>
  ///     launches a host: routing the agent this host already is back through it would recurse
  ///     into launching a host for it. This is the same runner every agent uses — one way to run
  ///     an agent, one mechanism — reached explicitly rather than through the orchestration seam.</summary>
  public InProcessAgentRuntime LocalRunner { get; }

  /// <summary>The container's live-child mailbox registry — the delivery view the
  ///     server resolves 'deliver' envelopes through, so wire-delivered steering lands
  ///     in the SAME box the running child's loop drains (W3).</summary>
  public ChildMailboxRegistry Mailboxes { get; }

  /// <summary>The SubAgent:Watchdog configuration the app shipped (W1.2): null when no
  ///     watchdog section was configured. The settings JSON is the only carrier; the
  ///     host never reads app configuration itself.</summary>
  public WatchdogSettings? Watchdog { get; }

  /// <summary>The host's ROOT turn runner (spec D6): the conversation's own agent runs in
  ///     this process, over THIS container's conversation and turn handler. Built with the
  ///     container — never per turn — because the root transcript sink seeds its persisted
  ///     baseline from the conversation's length at construction, and because filling
  ///     <see cref="RootSessionIdentity"/> before any turn is what makes that sink active
  ///     (the host never persisted a root row, so an unfilled identity leaves it inert).</summary>
  public RootRunHost? RootRun { get; }

  /// <summary>The options the host watchdog runs under: the shipped configuration
  ///     translated strictly, or <see cref="WatchdogOptions.Default"/> when no
  ///     watchdog section was configured. The single source of truth for the server's
  ///     watchdog construction — configured values govern exactly (W1.2).</summary>
  public WatchdogOptions EffectiveWatchdogOptions => Watchdog?.ToOptions() ?? WatchdogOptions.Default;

  /// <summary>Builds from the settings JSON the app writes before launching the host, plus
  ///     the app-owned database path (CLI arg — one host per app, sharing its database).
  ///     The container's <see cref="ChildMailboxRegistry"/> is exposed as
  ///     <see cref="Mailboxes"/> so the server's 'deliver' path resolves the SAME box
  ///     the running child's loop drains (FR-C2, W3 delivery fix).</summary>
  public static SessionHost Create(string settingsJsonPath, string databasePath)
  {
    EnsureSqliteNativeLibraryLoads();
    string json = File.ReadAllText(settingsJsonPath);
    AgentSettings deserialized = JsonSerializer.Deserialize<AgentSettings>(json, Options)
        ?? throw new InvalidOperationException("host settings deserialized to null.");
    // Strict boundary (W1.2 found this): STJ binds members ABSENT from the JSON to null
    // even though the record declares them required — the supervisor always serializes
    // the full settings, but a hand-written file must fail with a NAMED error here,
    // never a null-reference fault deep in composition.
    if (deserialized.OpenRouter is null || deserialized.SubAgents is null)
    {
      List<string> missing = [];
      if (deserialized.OpenRouter is null)
      {
        missing.Add("OpenRouter");
      }

      if (deserialized.SubAgents is null)
      {
        missing.Add("SubAgents");
      }
      throw new InvalidOperationException(
          $"host settings JSON is missing required member(s): {string.Join(", ", missing)}.");
    }

    AgentSettings settings = deserialized;

    string providerName = Providers.OpenRouter;
    string workspace = ResolveWorkspace(settings.WorkspaceRoot, settingsJsonPath);
    // The IDENTITY arrives beside the root (issue #317 D5): the app resolved it when the session
    // opened, and the host scopes every durable row to it. Falling back to the path is the
    // pre-#317 shape - correct for a workspace whose id IS its path, and the only honest answer
    // when no identity was shipped.
    string workspaceId = string.IsNullOrWhiteSpace(settings.WorkspaceId) ? workspace : settings.WorkspaceId;
    ModelConfig bootstrapModel = ModelConfig.Create(Providers.FallbackModelId(providerName), 32 * 1024, 0.7f, null, acceptsImageInput: FallbackModelCatalog.AcceptsImageInput(Providers.FallbackModelId(providerName))).Value!;

    ServiceProvider services = new ServiceCollection()
        .AddEThangAgentCore(
            settings, providerName, bootstrapModel,
            new AgentHostOptions(
                new FixedWorkspaceContext(workspaceId, workspace),
                new WorkspacePathResolver(workspace),
                [new SessionFilesPromptProvider(workspace, settings.SessionFilesGlobal, settings.SessionFilesWorkspace)]),
            // The host is a GUEST in the app's database: the app owns the schema. A
            // guest that migrates takes a schema-level lock on a file the app may be
            // writing, and on a busy database that lock times out ("database is
            // locked") - the failure the root-runner build swallowed, so every root
            // turn was refused with "this conversation host is not bound to a root
            // session" (2026-10-22). An EXISTING database is therefore opened as-is; a
            // missing one is the other case (nothing owns the schema yet), and the
            // host creates it.
            File.Exists(databasePath)
                ? AppDatabase.OpenExisting(databasePath)
                : new AppDatabase(databasePath),
            null)
            // Remote parity (skill-routing Task 12): the host replaces the core's
            // settings-sourced registration the SAME way the app factory does,
            // from the settings-carried stored lists, so remote children render
            // the identical always-on skills listing in their system prompt.
            .AddSingleton(new ResolvedSkillDirectories(AgentComposition.ResolveSkillDirectoriesFromSettings(settings)))
            // Trusted directories (issue #96) ride the settings JSON for the same reason
            // skill directories do: THIS container runs every production turn, so the
            // approved external roots must resolve here, not only in the app process.
            .AddSingleton(new ResolvedTrustedDirectories(
                AgentComposition.ResolveTrustedDirectoriesFromSettings(settings)))
            // ORCHESTRATION (issue #305 D1/D2): this host runs each agent in its OWN host
            // process. The in-process runtime it used to register ran every child inside THIS
            // process — one job object, one pipe, one scratch folder for all of them — which is
            // the condition the direction exists to end. The leaf executor (LocalRunner below)
            // is still the in-process runner: it runs the one agent this host owns, and every
            // agent uses it, so there is one way to run an agent rather than two.
            .AddHostLaunchingAgentRuntime(settings, databasePath)
            // The LEAF executor, registered separately from the orchestration seam so the server
            // can run this host's own agent without recursing into a host launch.
            .AddInProcessAgentRuntime(settings)
        .BuildServiceProvider();

    // The chain contract (issue #311): THIS container builds the system prompt for every
    // production turn, so the contract the app resolved has to be filled HERE or the prompt
    // would be silent in the only process that serves the model. Filled after the build
    // because the carrier is construction-phase glue, exactly like the session-file carrier.
    services.GetRequiredService<ChainContractCarrier>().Fill(settings.ChainContract);

    // Hot reload (issue #269): THIS container serves the model's skill_list/skill_view in
    // every production session, so THIS is where the watcher must be live. The app-side
    // factory's start never ran here - the host builds its own container - which left a
    // skill added mid-session invisible for the session's whole lifetime.
    AgentComposition.StartSkillHotReload(services);

    return new SessionHost(
        services,
        services.GetRequiredService<IAgentStore>(),
        services.GetRequiredService<IAgentRuntime>(),
        services.GetRequiredService<InProcessAgentRuntime>(),
        services.GetRequiredService<ChildMailboxRegistry>(),
        settings.Watchdog,
        BuildRootRun(services, settings.SessionId));
  }

  /// <summary>The host's own deployment must be able to load SQLite's native library.
  ///     A published sidecar ships WITHOUT its dependency manifest (the SDK publishes a
  ///     referenced project's assemblies but not its deps.json), and without that
  ///     manifest the runtime cannot resolve e_sqlite3.dll - so every turn faulted
  ///     with "The type initializer for 'Microsoft.Data.Sqlite.SqliteConnection' threw
  ///     an exception" while the reader looked for a product bug (2026-10-22). The
  ///     check touches NO file: it exercises the type initializer, which is what loads
  ///     the native library, and fails NAMED when the deployment is incomplete.
  /// </summary>
  private static void EnsureSqliteNativeLibraryLoads()
  {
    try
    {
      // Constructing a connection runs the type initializer (which loads the native
      // library) without opening a database.
      using SqliteConnection probe = new("Data Source=:memory:");
    }
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception ex)
    {
      // Named decision (CA1031): an incomplete deployment is an expected condition the
      // caller can act on, so it is a named error - never an escaping exception.
      throw new InvalidOperationException(
          "the conversation host could not load its SQLite native library, so it cannot use the app database: "
          + HostFault.Describe(ex)
          + " - the deployment is incomplete: the host's dependency manifest ("
          + "eThangAgent.ChildHost.deps.json) must sit next to its executable.",
          ex);
    }
#pragma warning restore CA1031 // Do not catch general exception types
  }

  /// <summary>Builds the host's ROOT turn runner when the settings name a root session
  ///     (spec D6), or null when they do not — a host with no bound session must not guess
  ///     which conversation a turn belongs to.
  ///     <para>Built ONCE, with the container: the runner fills
  ///     <c>RootSessionIdentity</c> so the container's root transcript sink stops being
  ///     inert, and the sink seeds its persisted baseline from the conversation's length at
  ///     construction — a per-turn runner would re-seed that baseline mid-conversation and
  ///     re-append messages the store already holds.</para></summary>
  private static RootRunHost? BuildRootRun(IServiceProvider services, string? sessionId)
  {
    return string.IsNullOrWhiteSpace(sessionId) || !Guid.TryParse(sessionId, out Guid parsed)
        ? null
        : new RootRunHost(services, new AgentId(parsed), services.GetRequiredService<Conversation>());
  }

  /// <summary>The workspace the host's container anchors at (D): the root the app
  ///     ships in the settings JSON when present - absolute only, a relative root is
  ///     a named startup error (strict boundaries; never a silent resolution against
  ///     a random current directory) - else the legacy fallback: the settings JSON's
  ///     own directory, kept for settings files written before the anchor existed.</summary>
  private static string ResolveWorkspace(string? workspaceRoot, string settingsJsonPath)
  {
    return workspaceRoot switch
    {
      { } root when string.IsNullOrWhiteSpace(root) =>
          Path.GetDirectoryName(settingsJsonPath) ?? AppContext.BaseDirectory,
      { } root when Path.IsPathRooted(root) => root,
      { } root => throw new InvalidOperationException(
          "WorkspaceRoot must be an absolute path: '" + root + "'."),
      _ => Path.GetDirectoryName(settingsJsonPath) ?? AppContext.BaseDirectory,
    };
  }

  private static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);

}
