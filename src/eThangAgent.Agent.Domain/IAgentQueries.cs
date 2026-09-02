using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Read side of the spawn CQRS split: status and result lookups over persisted agents.
///     Implemented by AgentQueries; faked in tests. Queries never mutate state.</summary>
public interface IAgentQueries
{
  /// <summary>Current record for an agent; unknown ids surface the store's NotFound failure.</summary>
  Task<Result<AgentRecord>> GetStatus(AgentId id, CancellationToken ct = default);

  /// <summary>Final report for an agent; running agents fail NotComplete, completed agents
  ///     yield their report verbatim.</summary>
  Task<Result<string>> GetResult(AgentId id, CancellationToken ct = default);

  /// <summary>The persisted children of one agent - the store's parent-link index. The
  ///     broadcast walks the same chain InterruptSubtree tears down, so the walk can be
  ///     synchronous over the read model.</summary>
  Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default);
}
