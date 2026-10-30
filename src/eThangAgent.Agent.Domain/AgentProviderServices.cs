namespace eThangAgent.AgentDomain;

/// <summary>Collaborator wiring the agent capability provider runs with: the spawn
///     command, the queries, the parent-context resolver, and the optional runtime,
///     graph handler, wake source, and clock. One value per session; keeps the
///     provider's constructor
///     parameter-light (S107) without loosening any null-guard — each required
///     member is validated where it is bound to a field.</summary>
/// <param name="SpawnCommand">Starts children as independent actors.</param>
/// <param name="Queries">Serves child status and results to the parent.</param>
/// <param name="ParentContext">Resolves the spawning parent's record per dispatch
///     (the running child when a child spawns, else the persisted root session).</param>
/// <param name="Runtime">Optional session runtime; null leaves wait/resume/cancel
///     unavailable (legacy wiring, tests).</param>
/// <param name="Fanout">Optional fan-out/fan-in graph handler (the application
///     layer's SpawnGraphHandler — the domain cannot see application types); null
///     disables agent.fanout.</param>
/// <param name="WakeSource">Optional wake hub for the wake-aware agent.wait; null
///     keeps the legacy pure-settlement await when no timeout is requested.</param>
/// <param name="Clock">Optional time source for the wait's soft timeout and the
///     status line's progress age; defaults to <see cref="TimeProvider.System"/>.</param>
/// <param name="Progress">Optional session progress ledger (issue #93): the read
///     surface agent.status uses to report a RUNNING child's latest mid-run activity.
///     Null (legacy wiring, tests) keeps the status line byte-identical.</param>
public sealed record AgentProviderServices(
    IAgentSpawnCommand SpawnCommand,
    IAgentQueries Queries,
    Func<AgentRecord> ParentContext,
    IAgentRuntime? Runtime = null,
    Func<AgentRecord, SpawnRequest[], CancellationToken, Task<string>>? Fanout = null,
    IWaitWakeSource? WakeSource = null,
    TimeProvider? Clock = null,
    ChildProgressLedger? Progress = null);
