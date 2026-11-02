namespace eThangAgent.AgentDomain;

/// <summary>Collaborator wiring the agent capability provider runs with: the queries
///     and the optional runtime, wake source, clock, and progress ledger. One value per
///     session; keeps the provider's constructor
///     parameter-light (S107) without loosening any null-guard — each required
///     member is validated where it is bound to a field.</summary>
/// <param name="Queries">Serves an agent's status and result.</param>
/// <param name="Runtime">Optional session runtime; null leaves wait/resume/cancel
///     unavailable (legacy wiring, tests).</param>
/// <param name="WakeSource">Optional wake hub for the wake-aware agent.wait; null
///     keeps the legacy pure-settlement await when no timeout is requested.</param>
/// <param name="Clock">Optional time source for the wait's soft timeout and the
///     status line's progress age; defaults to <see cref="TimeProvider.System"/>.</param>
/// <param name="Progress">Optional session progress ledger (issue #93): the read
///     surface agent.status uses to report a RUNNING child's latest mid-run activity.
///     Null (legacy wiring, tests) keeps the status line byte-identical.</param>
/// <param name="Launcher">Optional port that starts an agent (issue #338). Null leaves
///     agent.launch unavailable rather than appearing to start something: a host that
///     cannot create a session must say so, not report a launch that never happened.</param>
public sealed record AgentProviderServices(
    IAgentQueries Queries,
    IAgentRuntime? Runtime = null,
    IWaitWakeSource? WakeSource = null,
    TimeProvider? Clock = null,
    AgentProgressLedger? Progress = null,
    IAgentLaunchCommand? Launcher = null);
