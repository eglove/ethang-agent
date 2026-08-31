using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain;

/// <summary>Collaborator services a <see cref="SubAgentSpawner"/> runs a child with:
///     the provider factory, transcript persistence, the child tool registry, the
///     system-prompt provider, and the child budgets. One value per session; keeps
///     the spawner's constructor parameter-light (S107) without loosening any
///     null-guard — each member is validated where it is bound to a field.</summary>
public sealed record SubAgentServices(
    IModelProviderFactory Factory,
    IAgentStore Store,
    IToolRegistry Tools,
    ISystemPromptProvider SystemPrompt,
    SubAgentOptions Options,
    IAgentHeartbeat? Heartbeat = null,
    IAgentEvents? Events = null);

