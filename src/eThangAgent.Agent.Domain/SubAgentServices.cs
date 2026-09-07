using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain;

/// <summary>Collaborator services a <see cref="SubAgentSpawner"/> runs a child with:
///     the provider factory, transcript persistence, the child tool registry, the
///     system-prompt provider, and the child budgets. One value per session; keeps
///     the spawner's constructor parameter-light (S107) without loosening any
///     null-guard — each member is validated where it is bound to a field.</summary>
/// <param name="Factory">Creates the child's model provider from its resolved config.</param>
/// <param name="Store">Persists terminal states and transcript deltas.</param>
/// <param name="Tools">The session tool registry children run against (wrapped per run).</param>
/// <param name="SystemPrompt">Builds the system prompt every child turn carries.</param>
/// <param name="Options">Child budgets and defaults (default model, concurrency, depth).</param>
/// <param name="Heartbeat">Optional liveness beats; null in legacy wiring (tests).</param>
/// <param name="Events">Optional child event stream; null in legacy wiring (tests).</param>
/// <param name="Audit">Optional watchdog audit store for grant denials; null disables audit.</param>
/// <param name="InboxFor">Optional per-child mailbox resolver for mid-run steering; null
///     disables steering.</param>
/// <param name="AnchorScope">Ambient workspace anchor the spawner lifts around an
///     anchored child's run (worktree ladder, T7): the exec engine's workspace
///     delegate reads it ahead of the session workspace. Null wiring plus an
///     anchored contract is an infrastructure misconfiguration — the spawner fails
///     such a run loudly rather than anchoring silently nowhere; unanchored runs
///     never touch it.</param>
public sealed record SubAgentServices(
    IModelProviderFactory Factory,
    IAgentStore Store,
    IToolRegistry Tools,
    ISystemPromptProvider SystemPrompt,
    SubAgentOptions Options,
    IAgentHeartbeat? Heartbeat = null,
    IAgentEvents? Events = null,
    IWatchdogEventStore? Audit = null,
    Func<AgentId, IAgentInbox?>? InboxFor = null,
    IWorkspaceAnchorScope? AnchorScope = null);

