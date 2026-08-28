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
    SessionModelPreferences? Preferences = null,
    IReadOnlyList<string>? SelectableModels = null)
{
  public string ModelId => Model.ModelId;
}
