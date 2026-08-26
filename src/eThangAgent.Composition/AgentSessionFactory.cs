using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition;

/// <summary>Builds one isolated <see cref="AgentSession"/> per opened workspace.
///     The desktop shell calls this every time the user opens a directory; each
///     returned session owns its own DI container, conversation, path resolver,
///     workspace identity, and clarify channel, so agents can run concurrently
///     without sharing mutable state. Cross-cutting singletons — configuration,
///     the shared SQLite database, model settings — are supplied by the caller and
///     reused across sessions.</summary>
/// <remarks>Pass a shared <see cref="AppDatabase"/> so every opened session hits
///     the ONE app-owned SQLite file (migrated once); when omitted each session
///     container constructs its own connection over the default database path.</remarks>
public sealed class AgentSessionFactory(AgentSettings settings, string apiKey, ModelConfig defaultModel,
    AppDatabase? database = null)
{
  private readonly AgentSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));
  private readonly string _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
  private readonly ModelConfig _defaultModel = defaultModel ?? throw new ArgumentNullException(nameof(defaultModel));
  private readonly AppDatabase? _database = database;

  /// <summary>Creates a session rooted at <paramref name="workspaceRoot"/>. The
  ///     directory must exist; workspace identity is its full path, so reopening
  ///     the same directory resumes that workspace's durable state (state keys,
  ///     curated memories) rather than forking a second identity. Fails with a
  ///     structured error when bootstrap persistence fails.</summary>
  public async Task<Result<AgentSession>> CreateAsync(string workspaceRoot,
      IClarifyChannel clarifyChannel, CancellationToken ct = default)
  {
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

    ServiceProvider services = new ServiceCollection()
        .AddEThangAgentCore(_settings, _apiKey, _defaultModel,
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

      return Result.Success<AgentSession>(new AgentSession(
          services,
          bootstrapped.Value!,
          services.GetRequiredService<Conversation>(),
          services.GetRequiredService<SendMessageCommandHandler>(),
          services.GetRequiredService<RootSessionLifecycle>(),
          services.GetRequiredService<ModelConfig>(),
          workspaceRoot,
          clarifyChannel,
          services.GetRequiredService<IAgentInbox>(),
          services.GetRequiredService<IAgentRuntime>()));
    }
    catch
    {
      await services.DisposeAsync().ConfigureAwait(false);
      throw;
    }
  }
}
