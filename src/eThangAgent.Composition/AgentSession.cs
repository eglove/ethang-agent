using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition;

/// <summary>One opened agent session: an isolated slice of the composed core
///     rooted at a single workspace directory. Sessions share nothing mutable —
///     conversation, path resolution, workspace identity, and clarify channel are
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
    IClarifyChannel ClarifyChannel,
    IAgentInbox Inbox,
    IAgentRuntime ChildRuntime,
    SessionModelPreferences? Preferences = null)
{
  public string ModelId => Model.ModelId;

  /// <summary>Sink for out-of-band session notices (host health, orphan repair),
  ///     populated by the host UI after the session is constructed: the VM owns the
  ///     transcript, the session does not. Null = notices are dropped (headless hosts).
  ///     Thread-safe by contract: invoked from background supervisory paths.</summary>
  public Action<string>? NoticeSink { get; set; }

  /// <summary>Posts one out-of-band notice to the session surface.</summary>
  public void PostNotice(string message) => NoticeSink?.Invoke(message);
}
