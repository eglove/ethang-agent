using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition;

/// <summary>Builds one isolated <see cref="AgentSession"/> per opened workspace, wired for
///     ONE exclusively selected AI provider. The desktop shell calls this every time the
///     user opens a directory through the new-agent dialog; each returned session owns its
///     own DI container, conversation, path resolver, workspace identity, and clarify
///     channel, so agents can run concurrently without sharing mutable state. The session
///     stays on its provider until closed. Cross-cutting singletons — configuration, the
///     shared SQLite database, model settings — are supplied by the caller and reused
///     across sessions. <see cref="ResumeAsync"/> reopens a previously persisted root
///     session by id: its transcript hydrates the new container's conversation, so a
///     resumed session continues exactly where it stopped. A workspace holds MANY
///     sessions; resume targets one session id and never merges histories.</summary>
/// <remarks>Pass a shared <see cref="AppDatabase"/> so every opened session hits the
///     ONE app-owned SQLite file (migrated once); when omitted each session container
///     constructs its own connection over the default database path.</remarks>
public sealed class AgentSessionFactory(AgentSettings settings, AppDatabase? database = null)
{
  private readonly AgentSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));
  private readonly AppDatabase? _database = database;

  /// <summary>Returns a factory over the same database serving the updated settings.
  ///     Hosts call this when credentials change (the Desktop's Settings modal);
  ///     sessions already built keep the credentials they were created with.</summary>
  public AgentSessionFactory WithSettings(AgentSettings settings) =>
      new(settings ?? throw new ArgumentNullException(nameof(settings)), _database);

  /// <summary>Creates a session rooted at <paramref name="workspaceRoot"/> on the selected
  ///     <paramref name="providerName"/>. The directory must exist; workspace identity is
  ///     its full path, so reopening the same directory resumes that workspace's durable
  ///     state (state keys, curated memories) rather than forking a second identity — but
  ///     the CONVERSATION is always fresh: every open mints a new root session, never an
  ///     automatic resume. Fails with a structured error when the provider is unknown or
  ///     unconfigured, or when bootstrap persistence fails.</summary>
  public async Task<Result<AgentSession>> CreateAsync(string workspaceRoot, string providerName,
      IClarifyChannel clarifyChannel, CancellationToken ct = default)
  {
    Result<string> validated = ValidateProvider(workspaceRoot, providerName);
    if (!validated.IsSuccess)
    {
      return Result.Failure<AgentSession>(validated.Error);
    }

    if (clarifyChannel is null)
    {
      return Result.Failure<AgentSession>(new DomainError("InvalidChannel",
          "a clarify channel is required to create an agent session."));
    }

    string full = validated.Value;

    ServiceProvider services = BuildContainer(full, providerName, clarifyChannel, conversationSeed: null);
    try
    {
      IAgentStore store = services.GetRequiredService<IAgentStore>();
      Result<AgentId> bootstrapped = await RootSessionBootstrapper
          .PersistRootAsync(store, full, providerName, ct).ConfigureAwait(false);
      if (!bootstrapped.IsSuccess)
      {
        await services.DisposeAsync().ConfigureAwait(false);
        return Result.Failure<AgentSession>(bootstrapped.Error);
      }

      // Publish the root id to the container BEFORE the session is handed out: the
      // RootAgentResolver reads it lazily to persist ModelUsed on each selection, so it
      // must be set before the first turn can fire.
      services.GetRequiredService<RootSessionIdentity>().Id = bootstrapped.Value;

      // Provider lineups are no longer published at session open: the model picker
      // (host UI) fetches the catalog lazily when shown, for either provider.
      return Result.Success(BuildSession(services, bootstrapped.Value, full, providerName, clarifyChannel));
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
      IClarifyChannel clarifyChannel, CancellationToken ct = default)
  {
    if (clarifyChannel is null)
    {
      return Result.Failure<AgentSession>(new DomainError("InvalidChannel",
          "a clarify channel is required to resume an agent session."));
    }

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
    Result<IReadOnlyList<Message>> transcript = await store.GetTranscriptAsync(sessionId, ct).ConfigureAwait(false);
    if (!transcript.IsSuccess)
    {
      return Result.Failure<AgentSession>(transcript.Error);
    }

    string providerName = record.Provider;
    ServiceProvider services = BuildContainer(workspaceRoot, providerName, clarifyChannel, transcript.Value);
    try
    {
      services.GetRequiredService<RootSessionIdentity>().Id = sessionId;

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

      return Result.Success(BuildSession(services, sessionId, workspaceRoot, providerName, clarifyChannel));
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
          $"Unknown provider '{providerName}'. Known providers: {Providers.OpenRouter}, {Providers.Zai}."));
    }

    bool configured = providerName switch
    {
      Providers.OpenRouter => _settings.HasOpenRouter,
      Providers.Zai => _settings.HasZai,
      _ => false,
    };
    if (!configured)
    {
      return Result.Failure<string>(new DomainError("ProviderNotConfigured",
          $"Provider '{Providers.DisplayName(providerName)}' has no API key configured. Add one under Settings (gear icon) and open the agent again."));
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

  private ServiceProvider BuildContainer(string workspaceRoot, string providerName,
      IClarifyChannel clarifyChannel, IReadOnlyList<Message>? conversationSeed)
  {
    // Bootstrap model: the provider's default — z.ai's glm-5.3-flash or OpenRouter's
    // openrouter/auto placeholder — serving until the first turn resolves the real one
    // (intelligent selection, or the user's model picker choice restored from the
    // per-workspace preference).
    ModelConfig defaultModel = ModelConfig.Create(
        Providers.FallbackModelId(providerName), null, 32 * 1024, 0.7f).Value!;

    return new ServiceCollection()
        .AddEThangAgentCore(_settings, providerName, defaultModel,
            new AgentHostOptions(
                clarifyChannel,
                // Workspace identity IS the root path: state keys and curated
                // memories scope per opened directory, keyed in the shared DB.
                new FixedWorkspaceContext(workspaceRoot),
                new WorkspacePathResolver(workspaceRoot),
                [new WorkspaceInstructionsPromptProvider(workspaceRoot)]),
            _database,
            conversationSeed)
        .BuildServiceProvider();
  }

  private static AgentSession BuildSession(ServiceProvider services, AgentId rootId,
      string workspaceRoot, string providerName, IClarifyChannel clarifyChannel) => new(
      services,
      rootId,
      services.GetRequiredService<Conversation>(),
      services.GetRequiredService<SendMessageCommandHandler>(),
      services.GetRequiredService<RootSessionLifecycle>(),
      services.GetRequiredService<ModelConfig>(),
      workspaceRoot,
      providerName,
      clarifyChannel,
      services.GetRequiredService<IAgentInbox>(),
      services.GetRequiredService<IAgentRuntime>(),
      services.GetRequiredService<SessionModelPreferences>());
}
