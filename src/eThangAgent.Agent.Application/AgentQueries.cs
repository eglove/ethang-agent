using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

/// <summary>Read side of the spawn CQRS split: status and result queries over the agent store.
///     Queries have no side effects and never mutate persisted state.</summary>
public sealed class AgentQueries(IAgentStore store) : IAgentQueries
{
  private readonly IAgentStore _store = store ?? throw new ArgumentNullException(nameof(store));

  /// <summary>Current record for an agent; unknown ids surface the store's NotFound failure verbatim.</summary>
  public Task<Result<AgentRecord>> GetStatus(AgentId id, CancellationToken ct = default)
      => _store.GetAsync(id, ct);

  /// <summary>Final report for an agent. Running agents fail with NotComplete; completed agents
  ///     yield their report (NotFound-shaped when the report never landed); failed agents yield
  ///     their partial report or a reason-specific annotation line.</summary>
  public async Task<Result<string>> GetResult(AgentId id, CancellationToken ct = default)
  {
    Result<AgentRecord> record = await _store.GetAsync(id, ct).ConfigureAwait(false);
    if (!record.IsSuccess)
    {
      return Result.Failure<string>(record.Error);
    }

    AgentRecord agent = record.Value;
    return agent.Status switch
    {
      AgentStatus.Running => Result.Failure<string>(new DomainError("NotComplete",
          $"Agent '{id}' has not finished running. Check agent.status later.")),
      AgentStatus.Completed when agent.FinalReport is { } report => Result.Success(report),
      AgentStatus.Completed => Result.Failure<string>(new DomainError("NotFound",
          $"No agent exists with id '{id}'.")),
      AgentStatus.Failed when agent.FinalReport is { } partial => Result.Success(partial),
      AgentStatus.Failed => Result.Success(NoReportLine(agent.FailureReason)),
      AgentStatus.Interrupted => Result.Success(NoReportLine(agent.FailureReason)),
      _ => throw new InvalidOperationException($"Unknown agent status '{agent.Status}' for agent '{id}'."),
    };
  }

  private static string NoReportLine(AgentFailureReason? reason) => reason switch
  {
    AgentFailureReason.MaxIterations =>
        "Error [MaxIterations]: child agent hit the tool-iteration limit without a final report.",
    AgentFailureReason.Timeout =>
        "Error [Timeout]: child agent timed out before completing.",
    AgentFailureReason.ProviderError =>
        "Error [ProviderError]: agent failed without a report.",
    AgentFailureReason.Interrupted =>
        "Error [Interrupted]: the child agent was interrupted by the user before completing.",
    AgentFailureReason.Hung =>
        "Error [Hung]: the child agent was terminated by the watchdog after idle detection and a wrap-up retry.",
    AgentFailureReason.BudgetExhausted =>
        "Error [BudgetExhausted]: the child agent reached a budget hard ceiling and was terminated.",
    _ => throw new InvalidOperationException($"Unknown agent failure reason '{reason}'."),
  };
}
