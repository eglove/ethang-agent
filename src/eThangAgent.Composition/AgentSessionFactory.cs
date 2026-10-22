using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.AgentInfrastructure;
using eThangAgent.ComputerUse.ACL;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;
using eThangAgent.StateDomain;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using eThangAgent.Transport.ACL;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition;

/// <summary>Builds one isolated <see cref="AgentSession"/> per opened workspace, wired for
///     ONE exclusively selected AI provider. The desktop shell calls this every time the
///     user opens a directory through the new-agent dialog; each returned session owns its
///     own DI container, conversation, path resolver, and workspace identity, so agents
///     can run concurrently without sharing mutable state. The session
///     stays on its provider until closed. Cross-cutting singletons — configuration, the
///     shared SQLite database, model settings — are supplied by the caller and reused
///     across sessions. <see cref="ResumeAsync"/> reopens a previously persisted root
///     session by id: its transcript hydrates the new container's conversation, so a
///     resumed session continues exactly where it stopped. A workspace holds MANY
///     sessions; resume targets one session id and never merges histories.</summary>
/// <remarks>Pass a shared <see cref="AppDatabase"/> so every opened session hits the
///     ONE app-owned SQLite file (migrated once); when omitted each session container
///     constructs its own connection over the default database path.</remarks>
public sealed class AgentSessionFactory(AgentSettings settings, AppDatabase? database = null,
    ProcessMailboxLocator? mailboxLocator = null, Func<string>? hostExePath = null)
{
  private readonly AgentSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));
  private readonly AppDatabase? _database = database;

  /// <summary>Where the out-of-process conversation host executable lives; null uses the
  ///     default resolution (the repo's live build output, else the copy shipped next to
  ///     the app). A deployment seam, and the way a test pins the named-failure contract
  ///     for a host that cannot start.</summary>
  private readonly Func<string>? _hostExePath = hostExePath;

  // ONE process-wide cross-container locator (W3.2): every session this factory opens
  // contributes its live mailboxes here, so a link to another session's child resolves
  // instead of failing NotRunning. Lazily minted for single-session hosts.
  private readonly ProcessMailboxLocator _mailboxLocator = mailboxLocator ?? new();

  /// <summary>F4 (fix round 5): ONE process-wide computer-use provider for every
  ///     container this factory builds - two sessions over one workspace share the
  ///     broker, the ObservationLedger/FrameRegistry, and the per-root access cache.
  ///     Minted on first use: one registry + provider per FACTORY (the desktop process
  ///     holds one factory).</summary>
  private BrokerComputerAccessProvider? ComputerAccessProvider { get; set; }

  private BrokerComputerAccessProvider ResolveComputerAccessProvider() =>
      ComputerAccessProvider ??= new BrokerComputerAccessProvider(new BrokerRegistry(
          () => Path.Combine(AppContext.BaseDirectory, "eThangAgent.ComputerUse.Host.exe"),
          BrokerRegistry.DefaultPipeNameFor));

  /// <summary>Returns a factory over the same database serving the updated settings.
  ///     Hosts call this when credentials change (the Desktop's Settings modal);
  ///     sessions already built keep the credentials they were created with.</summary>
  public AgentSessionFactory WithSettings(AgentSettings settings) =>
      new(settings ?? throw new ArgumentNullException(nameof(settings)), _database, _mailboxLocator, _hostExePath);

  /// <summary>Creates a session rooted at <paramref name="workspaceRoot"/> on the selected
  ///     <paramref name="providerName"/>. The directory must exist; workspace identity is
  ///     its full path, so reopening the same directory resumes that workspace's durable
  ///     state (state keys, curated memories) rather than forking a second identity — but
  ///     the CONVERSATION is always fresh: every open mints a new root session, never an
  ///     automatic resume. Fails with a structured error when the provider is unknown or
  ///     unconfigured, or when bootstrap persistence fails.</summary>
  public async Task<Result<AgentSession>> CreateAsync(string workspaceRoot, string providerName,
      CancellationToken ct = default)
  {
    Result<string> validated = ValidateProvider(workspaceRoot, providerName);
    if (!validated.IsSuccess)
    {
      return Result.Failure<AgentSession>(validated.Error);
    }

    string full = validated.Value;

    (string? globalFiles, string? workspaceFiles) = await ReadSessionFilesAsync(full, ct).ConfigureAwait(false);
    (string? globalDirectories, string? workspaceDirectories) = await ReadSkillDirectoriesAsync(full, ct).ConfigureAwait(false);
    ServiceProvider services = BuildContainer(full, providerName, conversationSeed: null,
        ResolveBootstrapModel(providerName),
        new StoredPreferenceLists(globalFiles, workspaceFiles, globalDirectories, workspaceDirectories));
    services.GetService<SessionFilesCarrier>()?.Fill(globalFiles, workspaceFiles);
    try
    {
      IAgentStore store = services.GetRequiredService<IAgentStore>();
      // Creation-time model stamp (eval-runner gap fix): the bootstrap resolution already
      // holds the model that will serve turn one, so the persisted row records it as
      // FACT from the first read. Resume never re-stamps: a persisted ModelUsed is the
      // record of what actually served, not what the new container resolves.
      Result<AgentId> bootstrapped = await RootSessionBootstrapper
          .PersistRootAsync(store, full, providerName, modelUsed: ResolveBootstrapModel(providerName).ModelId, ct)
          .ConfigureAwait(false);
      if (!bootstrapped.IsSuccess)
      {
        await services.DisposeAsync().ConfigureAwait(false);
        return Result.Failure<AgentSession>(bootstrapped.Error);
      }

      // Publish the root id to the container BEFORE anything resolves it: the host
      // supervisor keys its pipe name, scratch folder, and settings JSON on the
      // conversation id (spec D6/D7), so orphan repair below cannot run without it — and
      // the RootAgentResolver reads it lazily to persist ModelUsed on each selection, so it
      // must be set before the first turn can fire.
      services.GetRequiredService<RootSessionIdentity>().Id = bootstrapped.Value;

      // Exact orphan repair at session open (FR-L8, R3.2): a persisted Running record
      // survives only if the host still owns it. A host that cannot start is a NAMED
      // failure, never an exception: the open path runs on the UI dispatcher, where an
      // escaping exception takes the whole process down (2026-10-21).
      try
      {
        await RepairOrphansAsync(services, bootstrapped.Value, ct).ConfigureAwait(false);
      }
      catch (OperationCanceledException) when (ct.IsCancellationRequested)
      {
        // The CALLER cancelled: propagate, never dress a cancellation as a failure.
        await services.DisposeAsync().ConfigureAwait(false);
        throw;
      }
      // Named decision (CA1031): every host startup fault - a missing executable, a
      // refused spawn, a pipe nobody serves - is an EXPECTED condition the caller can
      // act on, so it becomes a named error value instead of an exception.
#pragma warning disable CA1031, S2486 // Do not catch general exception types
      catch (Exception ex)
#pragma warning restore CA1031, S2486
      {
        await services.DisposeAsync().ConfigureAwait(false);
        return Result.Failure<AgentSession>(new DomainError("HostUnavailable",
            "the conversation host could not be started: " + ex.Message));
      }

      // Provider lineups are no longer published at session open: the model picker
      // (host UI) fetches the catalog lazily when shown, for either provider.
      return Result.Success(BuildSession(services, bootstrapped.Value, full, providerName));
    }
    catch
    {
      await services.DisposeAsync().ConfigureAwait(false);
      throw;
    }
  }

  /// <summary>Reopens a previously persisted root session by id: its persisted transcript
  ///     hydrates the new container's conversation, and a Completed row returns to Running.
  ///     The session resumes on its ORIGINAL provider and workspace — both come from the
  ///     persisted record, never from the caller. A workspace holds many sessions; only
  ///     the addressed session's history is loaded. Fails with a structured error when the
  ///     id is unknown, the record is a spawned child or predates workspace binding, the
  ///     provider is unknown or unconfigured, the workspace directory is gone, or the
  ///     transcript cannot be read.</summary>
  public async Task<Result<AgentSession>> ResumeAsync(AgentId sessionId,
      CancellationToken ct = default)
  {
    // Resolve the record before building anything: workspace and provider decide the
    // wiring, so a bad id must not leave a half-built container behind.
    SqliteAgentStore store = _database is null ? new SqliteAgentStore(new AppDatabase()) : new SqliteAgentStore(_database);
    Result<AgentRecord> loaded = await store.GetAsync(sessionId, ct).ConfigureAwait(false);
    if (!loaded.IsSuccess)
    {
      return Result.Failure<AgentSession>(loaded.Error);
    }

    AgentRecord record = loaded.Value;
    if (record.Depth != 0)
    {
      return Result.Failure<AgentSession>(new DomainError("NotResumable",
          $"agent {sessionId} is a spawned child (depth {record.Depth}); only root sessions can be resumed."));
    }

    if (record.WorkspaceId is null || record.Provider is null)
    {
      return Result.Failure<AgentSession>(new DomainError("NotResumable",
          $"agent {sessionId} was persisted before workspace binding and carries no workspace or provider; it cannot be resumed."));
    }

    Result<string> validated = ValidateProvider(record.WorkspaceId, record.Provider);
    if (!validated.IsSuccess)
    {
      return Result.Failure<AgentSession>(validated.Error);
    }

    string workspaceRoot = validated.Value;
    const string ResumeContractNotice = "[resume: tool contracts may have changed since this session was last open. The CURRENT build's tool definitions are authoritative — when a tool error describes the accepted parameters, trust the error over any tool listing or contract text quoted earlier in this transcript, and re-read the contract from the error itself.]";
    Result<IReadOnlyList<Message>> transcript = await store.GetTranscriptAsync(sessionId, ct).ConfigureAwait(false);
    if (!transcript.IsSuccess)
    {
      return Result.Failure<AgentSession>(transcript.Error);
    }

    string providerName = record.Provider;

    (string? globalFiles, string? workspaceFiles) = await ReadSessionFilesAsync(workspaceRoot, ct).ConfigureAwait(false);
    (string? globalDirectories, string? workspaceDirectories) = await ReadSkillDirectoriesAsync(workspaceRoot, ct).ConfigureAwait(false);
    ServiceProvider services = BuildContainer(workspaceRoot, providerName, transcript.Value,
        ResolveBootstrapModel(providerName),
        new StoredPreferenceLists(globalFiles, workspaceFiles, globalDirectories, workspaceDirectories));
    services.GetService<SessionFilesCarrier>()?.Fill(globalFiles, workspaceFiles);
    try
    {

      services.GetRequiredService<RootSessionIdentity>().Id = sessionId;
      // Resume contract notice: the hydrated transcript may carry tool listings and
      // contract text from an OLDER build (the session survived an app update), and
      // transcript history is append-only — nothing rewrites it. The wire schema is
      // rebuilt from the live registry every turn, so the CURRENT definitions are
      // what the provider serves; this notice says so explicitly, so the model never
      // trusts stale in-context contract text over a live tool error.
      services.GetRequiredService<Conversation>().AddSystemMessage(ResumeContractNotice);
      // The resumed conversation runs in its own host exactly as a fresh one does: attach
      // (launching it when absent) before the first turn can fire. A host that cannot
      // start is a NAMED failure, never an exception (the same contract as a fresh open).
      try
      {
        await AttachHostAsync(services, ct).ConfigureAwait(false);
      }
      catch (OperationCanceledException) when (ct.IsCancellationRequested)
      {
        // The CALLER cancelled: propagate, never dress a cancellation as a failure.
        await services.DisposeAsync().ConfigureAwait(false);
        throw;
      }
      // Named decision (CA1031): every host startup fault - a missing executable, a
      // refused spawn, a pipe nobody serves - is an EXPECTED condition the caller can
      // act on, so it becomes a named error value instead of an exception.
#pragma warning disable CA1031, S2486 // Do not catch general exception types
      catch (Exception ex)
#pragma warning restore CA1031, S2486
      {
        await services.DisposeAsync().ConfigureAwait(false);
        return Result.Failure<AgentSession>(new DomainError("HostUnavailable",
            "the conversation host could not be started: " + ex.Message));
      }
      // Re-open: clear a Completed row back to Running so status reflects the live session.
      Result<string> reopened = await store.UpdateAsync(record with
      {
        Status = AgentStatus.Running,
        CompletedAt = null,
      }, ct).ConfigureAwait(false);
      if (!reopened.IsSuccess)
      {
        await services.DisposeAsync().ConfigureAwait(false);
        return Result.Failure<AgentSession>(reopened.Error);
      }

      return Result.Success(BuildSession(services, sessionId, workspaceRoot, providerName));
    }
    catch
    {
      await services.DisposeAsync().ConfigureAwait(false);
      throw;
    }
  }

  /// <summary>Guard-style validation shared by create and resume: provider known and
  ///     configured, workspace non-empty and existing. Returns the full workspace path.</summary>
  private Result<string> ValidateProvider(string workspaceRoot, string providerName)
  {
    if (!Providers.IsKnown(providerName))
    {
      return Result.Failure<string>(new DomainError("UnknownProvider",
          $"Unknown provider '{providerName}'. Known providers: {Providers.OpenRouter}."));
    }

    bool configured = providerName switch
    {
      Providers.OpenRouter => _settings.HasOpenRouter,
      _ => false,
    };
    if (!configured)
    {
      return Result.Failure<string>(new DomainError("ProviderNotConfigured",
          $"Provider '{Providers.DisplayName(providerName)}' is not fully configured (API key). Complete its settings under Settings (gear icon) and open the agent again."));
    }

    if (string.IsNullOrWhiteSpace(workspaceRoot))
    {
      return Result.Failure<string>(new DomainError("InvalidWorkspace",
          "workspace root must be a non-empty directory path."));
    }

    if (!Directory.Exists(workspaceRoot))
    {
      return Result.Failure<string>(new DomainError("WorkspaceNotFound",
          $"workspace directory not found: '{workspaceRoot}'."));
    }

    // A statement between the final guard and this return keeps the guard shape the
    // analyzers enforce elsewhere (IDE0046 would fold guard+return into a conditional).
    string full = Path.GetFullPath(workspaceRoot);
    return Result.Success(full);
  }

  /// <summary>THE attach (R3.1/3.3): the wire runtime is born disconnected by design, and
  ///     this is its only production caller — launching this conversation's host if absent,
  ///     connecting, starting the settle pump, and reading the declared live set. Skipping
  ///     it leaves every start failing HostUnavailable and the declared set permanently
  ///     empty (the exact resolve-invoke seam the full-app E2E exists to guard). Every
  ///     session open and resume attaches, because every conversation runs in its own host
  ///     (spec D6/D7).</summary>
  private static async Task AttachHostAsync(ServiceProvider services, CancellationToken ct)
  {
    RemoteAgentRuntime runtime = services.GetRequiredService<RemoteAgentRuntime>();
    RemoteHostSupervisor supervisor = services.GetRequiredService<RemoteHostSupervisor>();
    _ = await supervisor.AttachAsync(runtime, ct).ConfigureAwait(false);
  }

  /// <summary>Runs exact orphan repair against the host's declared live set: a persisted
  ///     Running record survives only when the host still owns it, and everything else is
  ///     Failed(Interrupted). Failures of the repair never block session open: they surface
  ///     through the same validation path as any store fault.</summary>
  private static async Task RepairOrphansAsync(ServiceProvider services, AgentId rootId, CancellationToken ct)
  {
    await AttachHostAsync(services, ct).ConfigureAwait(false);
    RemoteAgentRuntime remote = services.GetRequiredService<RemoteAgentRuntime>();

    OrphanRepairHandler repair = new(
        services.GetRequiredService<IAgentStore>(),
        () => [],
        () => remote.DeclaredLiveChildren,
        services.GetRequiredService<IWatchdogEventStore>(),
        exempt: rootId,
        workspaceId: services.GetRequiredService<IWorkspaceContext>().WorkspaceId);
    await repair.RepairAsync(ct).ConfigureAwait(false);
  }

  /// <summary>Resolves the session's bootstrap model BEFORE any container is built:
  ///     the provider's static fallback id. The context window is unknown until the
  ///     catalog resolves it (null); the resolver re-resolves with the real window on
  ///     every turn, so the bootstrap config never serves a turn itself.</summary>
  private static ModelConfig ResolveBootstrapModel(string providerName) =>
      ModelConfig.Create(
          Providers.FallbackModelId(providerName), null, 32 * 1024, 0.7f,
          null,
          acceptsImageInput: FallbackModelCatalog.AcceptsImageInput(Providers.FallbackModelId(providerName))).Value!;

  /// <summary>Reads the two stored session-file lists (global, workspace) for one
  ///     workspace. Both are optional: a store miss returns nulls and the session
  ///     opens with nothing injected (truly empty by default).</summary>
  private async Task<(string? Global, string? Workspace)> ReadSessionFilesAsync(
      string workspaceRoot, CancellationToken ct)
  {
    SqliteAppPreferenceStore preferences = new(_database ?? new AppDatabase());
    string? global = await preferences.GetAsync(SessionFilePreferences.GlobalKey, ct).ConfigureAwait(false);
    string? workspace = await preferences.GetAsync(SessionFilePreferences.WorkspaceKey(workspaceRoot), ct).ConfigureAwait(false);
    return (global, workspace);
  }

  /// <summary>Reads the two stored skill-directory lists (global, workspace) for one
  ///     workspace - the same read pattern as <see cref="ReadSessionFilesAsync"/>,
  ///     over the skill-directory preference keys. Both are optional: a store miss
  ///     returns nulls and the session resolves no configured directories.</summary>
  private async Task<(string? Global, string? Workspace)> ReadSkillDirectoriesAsync(
      string workspaceRoot, CancellationToken ct)
  {
    SqliteAppPreferenceStore preferences = new(_database ?? new AppDatabase());
    string? global = await preferences.GetAsync(SkillDirectoryPreferences.GlobalKey, ct).ConfigureAwait(false);
    string? workspace = await preferences.GetAsync(SkillDirectoryPreferences.WorkspaceKey(workspaceRoot), ct).ConfigureAwait(false);
    return (global, workspace);
  }

  /// <summary>Resolves the configured skill directories from the two raw stored
  ///     lists (E-shape, same as session files). Both lists parse independently and
  ///     a parse FAILURE contributes an empty list - the raw strings still ride the
  ///     carrier, so a host overlay can re-read them (named decision: never half a
  ///     configured surface on a bad stored value). Enabled entries become
  ///     <see cref="SkillDirectory"/> values, global scope first, stored order kept.
  ///     DUPLICATE PATHS: a workspace path equal (case-insensitive, full-path
  ///     normalized) to an already-included global path is SKIPPED here with no
  ///     error - the global entry wins (Task 4 review ruling); the parse itself
  ///     validates shape only and never deduplicates.</summary>
  internal static List<SkillDirectory> ResolveSkillDirectories(
      string? globalStored, string? workspaceStored, string workspaceRoot)
  {
    List<SkillDirectory> resolved = [];
    HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

    // Global scope first, then workspace: the first scope to claim a path wins,
    // so a workspace duplicate of a global path is skipped here.
    AppendScopeEntries(resolved, seen, SkillDirectoryPreferences.Parse(globalStored),
        SkillDirectoryScope.Global, workspaceRoot);
    AppendScopeEntries(resolved, seen, SkillDirectoryPreferences.Parse(workspaceStored),
        SkillDirectoryScope.Workspace, workspaceRoot);

    return resolved;
  }

  /// <summary>Appends one scope's enabled, not-yet-seen entries to
  ///     <paramref name="resolved" />, stored order kept. A parse FAILURE contributes
  ///     nothing (the raw strings still ride the carrier for a host overlay). Full-path
  ///     normalization keeps the case-insensitive duplicate comparison on the resolved
  ///     form, not on whatever text the user typed.</summary>
  private static void AppendScopeEntries(List<SkillDirectory> resolved, HashSet<string> seen,
      Result<IReadOnlyList<SessionFileEntry>> parsed, SkillDirectoryScope scope, string workspaceRoot)
  {
    if (!parsed.IsSuccess)
    {
      return;
    }

    foreach (SessionFileEntry entry in parsed.Value)
    {
      if (!entry.Enabled)
      {
        continue;
      }

      string normalized = NormalizeDirectoryPath(entry.Path, workspaceRoot);
      if (seen.Add(normalized))
      {
        resolved.Add(new SkillDirectory(entry.Path, scope));
      }
    }
  }

  /// <summary>Full-path normalization for duplicate detection only: the resolved
  ///     <see cref="SkillDirectory"/> keeps the user's configured text; an absolute
  ///     path gets its canonical case-insensitive form, a relative path resolves
  ///     against the workspace root (never throws - an unresolvable path simply
  ///     stays itself, and directory existence is a load-time concern, not a
  ///     resolution concern).</summary>
  internal static string NormalizeDirectoryPath(string path, string workspaceRoot)
  {
    try
    {
      return Path.GetFullPath(path, string.IsNullOrWhiteSpace(workspaceRoot) ? Directory.GetCurrentDirectory() : workspaceRoot)
          .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
          .ToUpperInvariant();
    }
    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
    {
      return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
    }
  }

  private ServiceProvider BuildContainer(string workspaceRoot, string providerName,
      IReadOnlyList<Message>? conversationSeed,
      ModelConfig defaultModel,
      StoredPreferenceLists stored)
  {
    ServiceProvider services = new ServiceCollection()
        .AddEThangAgentCore(_settings, providerName, defaultModel,
            new AgentHostOptions(
                // Workspace identity IS the root path: state keys and curated
                // memories scope per opened directory, keyed in the shared DB.
                new FixedWorkspaceContext(workspaceRoot),
                new WorkspacePathResolver(workspaceRoot),
                [new SessionFilesPromptProvider(workspaceRoot, stored.GlobalSessionFiles, stored.WorkspaceSessionFiles)]),
            _database,
            new CompositionOverrides
            {
              ConversationSeed = conversationSeed,
              MailboxLocator = _mailboxLocator,
              ComputerAccessProvider = ResolveComputerAccessProvider(),
              HostExePath = _hostExePath,
            })
        // Skill directories resolve HERE - the resolution site. The parse validates
        // shape only; a workspace path equal (case-insensitive, full-path normalized)
        // to an already-included global path is skipped with no error: the global
        // entry wins (Task 4 review ruling; duplicate handling never lives in parse).
        // Replaces the core's empty registration (last registration wins), and the
        // carrier filled below is the core-registered singleton the RemoteHost block
        // overlays onto the settings JSON.
        .AddSingleton(new ResolvedSkillDirectories(
            ResolveSkillDirectories(stored.GlobalSkillDirectories, stored.WorkspaceSkillDirectories, workspaceRoot)))
        .BuildServiceProvider();
    services.GetRequiredService<SkillDirectoriesCarrier>().Fill(stored.GlobalSkillDirectories, stored.WorkspaceSkillDirectories);
    return RegisterContainerMailboxSource(services);
  }

  /// <summary>The raw stored preference lists a container build carries: the two
  ///     session-file lists and the two skill-directory lists, each (global, workspace).
  ///     Every member is optional - a store miss is null, and the session then opens
  ///     with nothing injected and no configured skill directories.</summary>
  private sealed record StoredPreferenceLists(
      string? GlobalSessionFiles,
      string? WorkspaceSessionFiles,
      string? GlobalSkillDirectories,
      string? WorkspaceSkillDirectories);

  /// <summary>Contributes THIS container's live-mailbox views to the process-wide
  ///     locator, carrying the container's event stream so a cross-container delivery
  ///     is audited on the target's side. Two sources when the session runs remote
  ///     children: the in-process registry, plus a wire-forwarding proxy source over the
  ///     remote runtime's OWNED children only (an id the host does not run resolves
  ///     none, keeping NotRunning honest). Runs once per built container; a source dies
  ///     with its container — a settled child's mailbox is unregistered, and a disposed
  ///     container's views resolve to none (the honest NotRunning answer).</summary>
  private static ServiceProvider RegisterContainerMailboxSource(ServiceProvider services)
  {
    ProcessMailboxLocator locator = services.GetRequiredService<ProcessMailboxLocator>();
    // The closures capture the RESOLVED instances, never the service provider: a probe
    // runs on any session's route path, possibly after this container was disposed —
    // a per-probe GetRequiredService would throw ObjectDisposedException there instead
    // of resolving none (the honest NotRunning answer). Captured singletons stay safe:
    // a disposed container's registry holds no mailboxes, and a dead remote runtime's
    // Deliver fails HostUnavailable inside its own fault boundary.
    ChildMailboxRegistry registry = services.GetRequiredService<ChildMailboxRegistry>();
    IAgentEvents events = services.GetRequiredService<IAgentEvents>();
    locator.AddSource(registry.MailboxFor, events);
    RemoteAgentRuntime? remote = services.GetService<RemoteAgentRuntime>();
    if (remote is not null)
    {
      locator.AddSource(id => remote.OwnedChildren.Contains(id.Value)
          ? new RemoteMailboxProxy(remote, id)
          : null, events);
    }

    return services;
  }

  private static AgentSession BuildSession(ServiceProvider services, AgentId rootId,
      string workspaceRoot, string providerName)
  {
    // Hot reload (spec #26): the watcher is idempotent-Start, so create AND resume
    // can call it; a second call on the same container is a no-op. Headless stubs
    // without the composition registrations skip it via GetService.
    services.GetService<SkillDirectoryWatcher>()?.Start();
    return new(
      services,
      rootId,
      services.GetRequiredService<Conversation>(),
      services.GetRequiredService<SendMessageCommandHandler>(),
      services.GetRequiredService<RootSessionLifecycle>(),
      services.GetRequiredService<ModelConfig>(),
      workspaceRoot,
      providerName,
      services.GetRequiredService<IAgentInbox>(),
      services.GetRequiredService<IAgentRuntime>(),
      services.GetRequiredService<SessionModelPreferences>())
    {
      // Shell access is registered by the core composition; a bare session container
      // (test stubs) without it yields a null runner and the ! surface reports unavailable.
      CommandRunner = services.GetService<IUserCommandRunner>(),
      // The shared invocation core (spec #28) rides the session beside the runner:
      // the Desktop's slash input resolves through it, same object the tool uses.
      SkillInvocation = services.GetService<SkillInvocationService>(),
      // The verbatim system prompt rides the session so hosts can show the user
      // exactly what the agent receives. Build() is a pure render (session-file
      // reads happen at container build); building it here keeps the conversation
      // untouched - the prompt never enters it.
      SystemPrompt = services.GetRequiredService<ISystemPromptProvider>().Build(),
    };
  }
}

