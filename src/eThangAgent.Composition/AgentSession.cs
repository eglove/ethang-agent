using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition;

/// <summary>One opened agent session: an isolated slice of the composed core
///     rooted at a single workspace directory. Sessions share nothing mutable —
///     conversation, path resolution, and workspace identity are
///     per-session — so several can run concurrently inside one process (one per
///     open agent tab in the desktop shell). The SQLite database IS shared by
///     design: rows are keyed by workspace id. The session is wired for exactly
///     one AI provider (<see cref="ProviderName"/>) for its whole lifetime.</summary>
public sealed record AgentSession(
    ServiceProvider Services,
    AgentId RootId,
    Conversation Conversation,
    SendMessageCommandHandler Handler,
    RootSessionLifecycle Lifecycle,
    ModelConfig Model,
    string WorkspaceRoot,
    string ProviderName,
    IAgentInbox Inbox,
    IAgentRuntime ChildRuntime,
    SessionModelPreferences? Preferences = null)
{
  public string ModelId => Model.ModelId;

  /// <summary>The session's rendered system prompt - the verbatim text every provider
  ///     call sends (skills bootstrap, persona, guides, configured session files).
  ///     Surfaces carry it to the user so nothing the agent receives is hidden.
  ///     Rendered once at session creation from the container's prompt providers.</summary>
  public string SystemPrompt { get; init; } = string.Empty;

  /// <summary>Runs ! commands (user-side shell utilities) against this session's
  ///     workspace: persists the run, appends the system message. Null when the host
  ///     did not wire shell access (headless stubs).</summary>
  public IUserCommandRunner? CommandRunner { get; init; }

  /// <summary>Sink for out-of-band session notices (host health, orphan repair),
  ///     populated by the host UI after the session is constructed: the VM owns the
  ///     transcript, the session does not. Null = notices are dropped (headless hosts).
  ///     Thread-safe by contract: invoked from background supervisory paths.</summary>
  public Action<string>? NoticeSink { get; set; }

  /// <summary>Posts one out-of-band notice to the session surface.</summary>
  public void PostNotice(string message) => NoticeSink?.Invoke(message);
}
