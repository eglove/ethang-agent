using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.ComputerUse.ACL;
using eThangAgent.ConversationDomain;
using eThangAgent.Lsp.ACL;
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
    Func<string>? hostExePath = null)
{
  private readonly AgentSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));
  private readonly AppDatabase? _database = database;

  /// <summary>Where the out-of-process conversation host executable lives; null uses the
  ///     default resolution (the repo's live build output, else the copy shipped next to
  ///     the app). A deployment seam, and the way a test pins the named-failure contract
  ///     for a host that cannot start.</summary>
  private readonly Func<string>? _hostExePath = hostExePath;

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
      new(settings ?? throw new ArgumentNullException(nameof(settings)), _database, _hostExePath);

  /// <summary>Creates a session rooted at <paramref name="workspaceRoot"/> on the selected
  ///     <paramref name="providerName"/>. The directory must exist; workspace identity is
  ///     its full path, so reopening the same directory resumes that workspace's durable
  ///     state (state keys, plan records) rather than forking a second identity — but
  ///     the CONVERSATION is always fresh: every open mints a new root session, never an
  ///     automatic resume. Fails with a structured error when the provider is unknown or
  ///     unconfigured, or when bootstrap persistence fails.</summary>
  public async Task<Result<AgentSession>> CreateAsync(string workspaceRoot, string providerName,
      IReadOnlyList<Message>? conversationSeed = null, CancellationToken ct = default)
    => await CreateCoreAsync(workspaceRoot, providerName, conversationSeed, chainContract: null,
        anchorRoot: null, ct).ConfigureAwait(false);

  /// <summary>The same creation, told which directory the session's work happens in when that
  ///     is not the workspace itself (a group member runs in its own git worktree).
  ///     <para>TWO FACTS, DELIBERATELY. <paramref name="workspaceRoot"/> is the workspace the
  ///     session BELONGS to - its identity, its durable records, its tab - and
  ///     <paramref name="anchorRoot"/> is the checkout its scripts and tools resolve against.
  ///     A member's worktree is a second checkout OF THE SAME WORKSPACE, so collapsing the two
  ///     would either fork the workspace's records onto a scratch path or leave the member
  ///     writing into the shared checkout its siblings are also writing into.</para>
  ///     <para>NULL IS THE ORDINARY LAUNCH and behaves exactly as before this overload existed:
  ///     the session resolves against its workspace root.</para></summary>
  /// <param name="workspaceRoot">The workspace directory the session belongs to.</param>
  /// <param name="providerName">The provider id.</param>
  /// <param name="conversationSeed">The seeded prefix, or null.</param>
  /// <param name="chainContract">The chain this session executes a step of, or null.</param>
  /// <param name="anchorRoot">The checkout the session works in, or null for the workspace.</param>
  /// <param name="ct">Cancellation.</param>
  public Task<Result<AgentSession>> CreateAsync(string workspaceRoot, string providerName,
      IReadOnlyList<Message>? conversationSeed, ChainContract? chainContract, string? anchorRoot,
      CancellationToken ct)
    => CreateCoreAsync(workspaceRoot, providerName, conversationSeed, chainContract, anchorRoot, ct);

  /// <summary>The same creation, carrying the chain contract the session runs under (issue
  ///     #311).
  ///     <para>WHY IT IS A PARAMETER AND NOT A SECOND FACTORY: which chain step a session is
  ///     belongs to THAT session, exactly like the workspace root and the conversation seed —
  ///     the factory serves many sessions over one workspace, so a contract held on the factory
  ///     would be one session's identity leaking onto every other.</para>
  ///     <para>A CONTRACT WITHOUT A SEED IS LEGAL and is the shape the mechanical advance uses:
  ///     the seed is the brief (a message) and the contract is the standing rule (prompt text),
  ///     and a caller that has only one of them still starts a correct session.</para></summary>
  /// <param name="workspaceRoot">The workspace directory.</param>
  /// <param name="providerName">The provider id.</param>
  /// <param name="conversationSeed">The seeded prefix, or null.</param>
  /// <param name="chainContract">The chain this session executes a step of, or null.</param>
  /// <param name="ct">Cancellation.</param>
  public Task<Result<AgentSession>> CreateAsync(string workspaceRoot, string providerName,
      IReadOnlyList<Message>? conversationSeed, ChainContract? chainContract, CancellationToken ct)
    => CreateCoreAsync(workspaceRoot, providerName, conversationSeed, chainContract, anchorRoot: null, ct);

  /// <summary>The creation itself, over both directories: the workspace the session belongs to
  ///     and the checkout it works in. <paramref name="anchorRoot"/> null means the workspace
  ///     root.</summary>
  private async Task<Result<AgentSession>> CreateCoreAsync(string workspaceRoot, string providerName,
      IReadOnlyList<Message>? conversationSeed, ChainContract? chainContract, string? anchorRoot,
      CancellationToken ct)
  {
    Result<LaunchPaths> validated = ValidateLaunch(workspaceRoot, providerName, anchorRoot);
    if (!validated.IsSuccess)
    {
      return Result.Failure<AgentSession>(validated.Error);
    }

    string full = validated.Value.WorkspaceRoot;
    string anchor = validated.Value.AnchorRoot;

    (string? globalFiles, string? workspaceFiles) = await ReadSessionFilesAsync(full, ct).ConfigureAwait(false);
    (string? globalDirectories, string? workspaceDirectories) = await ReadSkillDirectoriesAsync(full, ct).ConfigureAwait(false);
    string? trustedDirectories = await ReadTrustedDirectoriesAsync(full, ct).ConfigureAwait(false);
    // A workspace IS its directory path: nothing is written into the user's directory to
    // identify it, so the identity is derived rather than resolved.
    string workspaceId = WorkspaceIdentity.For(full);

    ServiceProvider services = BuildContainer(workspaceId, anchor, providerName, conversationSeed,
        ResolveBootstrapModel(providerName),
        new StoredPreferenceLists(globalFiles, workspaceFiles, globalDirectories, workspaceDirectories,
            trustedDirectories));
    services.GetService<SessionFilesCarrier>()?.Fill(globalFiles, workspaceFiles);
    // The chain contract (issue #311) is filled BEFORE the session is handed out: the prompt
    // provider reads it at construction, so a fill after the first turn would leave the prompt
    // silent for exactly the session it describes.
    services.GetService<ChainContractCarrier>()?.Fill(chainContract);
    // THE SESSION'S CHECKOUT IS AMBIENT WHILE IT IS BEING BUILT, and only then: the exec engine
    // and the completion gate read the anchor scope, so setting it here is what makes a group
    // member's scripts run in its own worktree rather than in the checkout its siblings write.
    // IT IS CLEARED BEFORE THIS METHOD RETURNS, because the scope is an AsyncLocal on the flow
    // that built this session - an anchor left set would travel into whatever that flow does
    // next, and the next launch on it would inherit a checkout it does not own.
    IWorkspaceAnchorScope? scope = services.GetService<IWorkspaceAnchorScope>();
    if (!string.Equals(anchor, full, StringComparison.OrdinalIgnoreCase))
    {
      SetAnchor(scope, anchor);
    }
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

      // The seed (the compaction handoff's summary, issue #234) is persisted EXPLICITLY:
      // the transcript sink's baseline counts the seeded prefix as already-persisted
      // (it seeds its counter from the conversation's length at construction), so
      // without this the seed would never reach the store and a resume would lose it.
      // The prefix is read from the CONTAINER'S conversation (finding 9): the sink's
      // baseline is defined by that conversation's length, so the persisted prefix must
      // be exactly what it holds — the caller's list is only the construction input.
      if (conversationSeed is { Count: > 0 })
      {
        IReadOnlyList<Message> seededPrefix = services.GetRequiredService<Conversation>().Messages;
        foreach (Message message in seededPrefix)
        {
          Result<string> appended = await store.AppendMessageAsync(bootstrapped.Value, message, ct)
              .ConfigureAwait(false);
          if (!appended.IsSuccess)
          {
            await services.DisposeAsync().ConfigureAwait(false);
            return Result.Failure<AgentSession>(appended.Error);
          }
        }
      }

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
      return Result.Success(BuildSession(services, bootstrapped.Value, anchor, providerName, []));
    }
    catch
    {
      await services.DisposeAsync().ConfigureAwait(false);
      throw;
    }
    finally
    {
      // THE ANCHOR IS AMBIENT FOR THE BUILD, NOT FOR THE FLOW (see above): leaving it set
      // would hand a member's checkout to the next launch that runs on this flow.
      SetAnchor(scope, null);
    }
  }

  /// <summary>Sets the ambient checkout, or clears it when <paramref name="anchor"/> is null.
  ///     A container that wired no scope has nothing to set.</summary>
  /// <param name="scope">The container's anchor scope, or null.</param>
  /// <param name="anchor">The checkout, or null to clear it.</param>
  private static void SetAnchor(IWorkspaceAnchorScope? scope, string? anchor)
    => scope?.Current = anchor;

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

    // Every record is a session (issue #334): the depth test that refused to resume a child
    // is gone with the parent-child link, so the binding below is the whole resumability rule.
    AgentRecord record = loaded.Value;
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
    string? trustedDirectories = await ReadTrustedDirectoriesAsync(workspaceRoot, ct).ConfigureAwait(false);
    // A resumed session keeps ITS OWN identity: the persisted record names the workspace the
    // conversation belongs to, and re-resolving would mint a different id for a directory whose
    // marker was deleted - forking a live session's records away from the workspace it ran in.
    ServiceProvider services = BuildContainer(record.WorkspaceId, workspaceRoot, providerName, transcript.Value,
        ResolveBootstrapModel(providerName),
        new StoredPreferenceLists(globalFiles, workspaceFiles, globalDirectories, workspaceDirectories,
            trustedDirectories));
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

      return Result.Success(BuildSession(services, sessionId, workspaceRoot, providerName, []));
    }
    catch
    {
      await services.DisposeAsync().ConfigureAwait(false);
      throw;
    }
  }

  /// <summary>The two directories a launch is rooted at, validated together: the workspace the
  ///     session belongs to and the checkout its work happens in.</summary>
  /// <param name="WorkspaceRoot">The full workspace directory.</param>
  /// <param name="AnchorRoot">The checkout the session works in; the workspace when none was
  ///     asked for.</param>
  private sealed record LaunchPaths(string WorkspaceRoot, string AnchorRoot);

  /// <summary>Validates a launch's provider, workspace, and checkout in one step, so the
  ///     creation path states one refusal instead of three.</summary>
  /// <param name="workspaceRoot">The workspace directory the session belongs to.</param>
  /// <param name="providerName">The provider id.</param>
  /// <param name="anchorRoot">The checkout the session works in, or null for the workspace.</param>
  private Result<LaunchPaths> ValidateLaunch(string workspaceRoot, string providerName,
      string? anchorRoot)
  {
    Result<string> validated = ValidateProvider(workspaceRoot, providerName);
    if (!validated.IsSuccess)
    {
      return Result.Failure<LaunchPaths>(validated.Error);
    }

    Result<string> anchor = ResolveAnchor(anchorRoot, validated.Value);
    return anchor.IsSuccess
        ? Result.Success(new LaunchPaths(validated.Value, anchor.Value))
        : Result.Failure<LaunchPaths>(anchor.Error);
  }

  /// <summary>The checkout a session works in: the workspace itself when none was asked for, and
  ///     a directory that must exist - a session whose every tool path fails is worse than a
  ///     refused launch.</summary>
  /// <param name="anchorRoot">The requested checkout, or null.</param>
  /// <param name="workspaceRoot">The full workspace directory.</param>
  private static Result<string> ResolveAnchor(string? anchorRoot, string workspaceRoot) =>
      anchorRoot is null
          ? Result.Success(workspaceRoot)
          : ExistingDirectory(anchorRoot);

  private static Result<string> ExistingDirectory(string path) =>
      Directory.Exists(path)
          ? Result.Success(Path.GetFullPath(path))
          : Result.Failure<string>(new DomainError("AnchorNotFound",
              $"the checkout this session would work in does not exist: '{path}'."));

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
      ModelConfig.Create(Providers.FallbackModelId(providerName), 32 * 1024, 0.7f, null, acceptsImageInput: FallbackModelCatalog.AcceptsImageInput(Providers.FallbackModelId(providerName))).Value!;

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

  /// <summary>Reads one workspace's stored trusted-directory list (issue #96). Optional
  ///     like every other launch preference: a store miss returns null and the session
  ///     opens with no external roots approved.</summary>
  private async Task<string?> ReadTrustedDirectoriesAsync(string workspaceRoot, CancellationToken ct)
  {
    SqliteAppPreferenceStore preferences = new(_database ?? new AppDatabase());
    return await preferences.GetAsync(TrustedDirectoryPreferences.WorkspaceKey(workspaceRoot), ct).ConfigureAwait(false);
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

  /// <summary>Builds one session container rooted at <paramref name="anchorRoot"/> - the
  ///     directory this session's work happens in, which is the workspace root for every
  ///     session but a group member.</summary>
  /// <param name="workspaceId">The stable workspace identity every durable row keys on.</param>
  /// <param name="anchorRoot">The checkout the session works in.</param>
  /// <param name="providerName">The provider id.</param>
  /// <param name="conversationSeed">The seeded prefix, or null.</param>
  /// <param name="defaultModel">The bootstrap model.</param>
  /// <param name="stored">The raw stored preference lists.</param>
  private ServiceProvider BuildContainer(string workspaceId, string anchorRoot, string providerName,
      IReadOnlyList<Message>? conversationSeed,
      ModelConfig defaultModel,
      StoredPreferenceLists stored)
  {
    ServiceProvider services = new ServiceCollection()
        .AddEThangAgentCore(_settings, providerName, defaultModel,
            new AgentHostOptions(
                // The IDENTITY and the ROOT are two facts since #317: durable rows scope on the
                // id (which survives a move) and path-rooted seams take the directory.
                new FixedWorkspaceContext(workspaceId, anchorRoot),
                new WorkspacePathResolver(anchorRoot),
                [new SessionFilesPromptProvider(anchorRoot, stored.GlobalSessionFiles, stored.WorkspaceSessionFiles)]),
            _database,
            new CompositionOverrides
            {
              ConversationSeed = conversationSeed,
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
            ResolveSkillDirectories(stored.GlobalSkillDirectories, stored.WorkspaceSkillDirectories, anchorRoot)))
        // Trusted directories (issue #96) override the core's settings-sourced
        // registration for the same reason the skill directories do above: the value was
        // just READ from the store here, and this is the site that owns preference
        // resolution. The carrier filled below is the core-registered singleton the
        // RemoteHost block overlays onto the settings JSON, so the host process - which
        // runs every production turn - resolves the identical roots.
        .AddSingleton(new ResolvedTrustedDirectories(
            TrustedDirectoryPreferences.Resolve(stored.TrustedDirectories)))
        .BuildServiceProvider();
    services.GetRequiredService<SkillDirectoriesCarrier>().Fill(stored.GlobalSkillDirectories, stored.WorkspaceSkillDirectories);
    services.GetRequiredService<TrustedDirectoriesCarrier>().Fill(stored.TrustedDirectories);
    return services;
  }

  /// <summary>The raw stored preference lists a container build carries: the two
  ///     session-file lists and the two skill-directory lists, each (global, workspace).
  ///     Every member is optional - a store miss is null, and the session then opens
  ///     with nothing injected and no configured skill directories.</summary>
  private sealed record StoredPreferenceLists(
      string? GlobalSessionFiles,
      string? WorkspaceSessionFiles,
      string? GlobalSkillDirectories,
      string? WorkspaceSkillDirectories,
      string? TrustedDirectories);

  private static AgentSession BuildSession(ServiceProvider services, AgentId rootId,
      string workspaceRoot, string providerName, IReadOnlyList<string> pendingNotices)
  {
    // Hot reload (spec #26, issue #269): the shared seam - idempotent, so create AND
    // resume can call it, and a headless stub without the registrations skips silently.
    // The ChildHost calls the SAME seam at its own container build, which is the
    // container that actually serves the model in production.
    AgentComposition.StartSkillHotReload(services);
    // Language server (issue #269 pattern): the same shared seam - the daemon and
    // server start warming at workspace start, not on the first query. The ChildHost
    // calls the SAME seam at its own container build.
    AgentComposition.StartLspDaemon(services);
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
      // The language-server status the daemon announces on (plan #143 step 6): the SAME
      // host instance the container's tools resolve, so what the model's queries trigger
      // is what the status bar shows.
      LspServerStatus = services.GetService<LspDaemonHost>()?.ServerStatus,
      // The shared invocation core (spec #28) rides the session beside the runner:
      // the Desktop's slash input resolves through it, same object the tool uses.
      SkillInvocation = services.GetService<SkillInvocationService>(),
      // The verbatim system prompt rides the session so hosts can show the user
      // exactly what the agent receives. Build() is a pure render (session-file
      // reads happen at container build); building it here keeps the conversation
      // untouched - the prompt never enters it.
      SystemPrompt = services.GetRequiredService<ISystemPromptProvider>().Build(),
      // Construction-time notices (issue #317 D5/D6): a workspace that moved or was copied says
      // so once, when it is opened. The sink is attached by the host UI after construction, so
      // they wait here rather than being posted into nothing.
      PendingNotices = pendingNotices,
    };
  }
}

