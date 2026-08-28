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
///     across sessions.</summary>
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
  ///     state (state keys, curated memories) rather than forking a second identity.
  ///     Fails with a structured error when the provider is unknown or unconfigured, or
  ///     when bootstrap persistence fails.</summary>
  public async Task<Result<AgentSession>> CreateAsync(string workspaceRoot, string providerName,
      IClarifyChannel clarifyChannel, CancellationToken ct = default)
  {
    if (!Providers.IsKnown(providerName))
    {
      return Result.Failure<AgentSession>(new DomainError("UnknownProvider",
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
      return Result.Failure<AgentSession>(new DomainError("ProviderNotConfigured",
          $"Provider '{Providers.DisplayName(providerName)}' has no API key configured. Add one under Settings (gear icon) and open the agent again."));
    }

    if (string.IsNullOrWhiteSpace(workspaceRoot))
    {
      return Result.Failure<AgentSession>(new DomainError("InvalidWorkspace",
          "workspace root must be a non-empty directory path."));
    }

    if (!Directory.Exists(workspaceRoot))
    {
      return Result.Failure<AgentSession>(new DomainError("WorkspaceNotFound",
          $"workspace directory not found: '{workspaceRoot}'."));
    }

    if (clarifyChannel is null)
    {
      return Result.Failure<AgentSession>(new DomainError("InvalidChannel",
          "a clarify channel is required to create an agent session."));
    }

    workspaceRoot = Path.GetFullPath(workspaceRoot);

    // Bootstrap model: the provider's default — z.ai's glm-5.3-flash or OpenRouter's
    // openrouter/auto placeholder — serving until the first turn resolves the real one
    // (intelligent selection, or the user's model picker choice restored from the
    // per-workspace preference).
    ModelConfig defaultModel = ModelConfig.Create(
        Providers.FallbackModelId(providerName), null, 32 * 1024, 0.7f).Value!;

    ServiceProvider services = new ServiceCollection()
        .AddEThangAgentCore(_settings, providerName, defaultModel,
            new AgentHostOptions(
                clarifyChannel,
                // Workspace identity IS the root path: state keys and curated
                // memories scope per opened directory, keyed in the shared DB.
                new FixedWorkspaceContext(workspaceRoot),
                new WorkspacePathResolver(workspaceRoot),
                [new WorkspaceInstructionsPromptProvider(workspaceRoot)]),
            _database)
        .BuildServiceProvider();

    try
    {
      IAgentStore store = services.GetRequiredService<IAgentStore>();
      Result<AgentId> bootstrapped = await RootSessionBootstrapper.PersistRootAsync(store, ct).ConfigureAwait(false);
      if (!bootstrapped.IsSuccess)
      {
        await services.DisposeAsync().ConfigureAwait(false);
        return Result.Failure<AgentSession>(bootstrapped.Error!);
      }

      // Publish the root id to the container BEFORE the session is handed out: the
      // RootAgentResolver reads it lazily to persist ModelUsed on each selection, so it
      // must be set before the first turn can fire.
      services.GetRequiredService<RootSessionIdentity>().Id = bootstrapped.Value!;

      // Provider lineups are no longer published at session open: the model picker
      // (host UI) fetches the catalog lazily when shown, for either provider.
      return Result.Success(new AgentSession(
          services,
          bootstrapped.Value!,
          services.GetRequiredService<Conversation>(),
          services.GetRequiredService<SendMessageCommandHandler>(),
          services.GetRequiredService<RootSessionLifecycle>(),
          services.GetRequiredService<ModelConfig>(),
          workspaceRoot,
          providerName,
          clarifyChannel,
          services.GetRequiredService<IAgentInbox>(),
          services.GetRequiredService<IAgentRuntime>(),
          services.GetRequiredService<SessionModelPreferences>()));
    }
    catch
    {
      await services.DisposeAsync().ConfigureAwait(false);
      throw;
    }
  }
}
