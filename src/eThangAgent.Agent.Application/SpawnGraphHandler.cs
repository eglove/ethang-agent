using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

/// <summary>Declarative spawn graph (step 10, D12): materializes children through the
///     NORMAL spawn command (no new runtime machinery) and joins their outcomes via
///     WhenSettledAsync. One failed member fails the join; per-member receipts list every
///     terminal state so nothing is silently dropped (A3).</summary>
public sealed class SpawnGraphHandler(IAgentSpawnCommand spawnCommand, IAgentRuntime runtime)
{
  private readonly IAgentSpawnCommand _spawnCommand = spawnCommand ?? throw new ArgumentNullException(nameof(spawnCommand));
  private readonly IAgentRuntime _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

  public async Task<Result<SpawnGraphOutcome>> ExecuteAsync(AgentRecord parent, SpawnGraphRequest request,
      CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(parent);
    ArgumentNullException.ThrowIfNull(request);
    if (request.Children.Count == 0)
    {
      return Result.Failure<SpawnGraphOutcome>(new DomainError("InvalidSpawnRequest",
          "a spawn graph requires at least one child."));
    }

    List<AgentId> ids = [];
    List<string> startReceipts = [];
    foreach (SpawnRequest child in request.Children)
    {
      Result<AgentId> started = await _spawnCommand.Execute(parent, child, ct).ConfigureAwait(false);
      if (started.IsSuccess)
      {
        ids.Add(started.Value);
      }
      else
      {
        startReceipts.Add("spawn-failed: [" + started.Error.Code + "] " + started.Error.Message);
        if (request.Join.FailFast)
        {
          return Result.Failure<SpawnGraphOutcome>(new DomainError("SpawnFailed",
              "fail-fast: " + started.Error.Message));
        }
      }
    }

    List<MemberReceipt> receipts = [];
    foreach (AgentId id in ids)
    {
      Result<AgentRunOutcome> outcome = await _runtime.WhenSettledAsync(id, ct).ConfigureAwait(false);
      receipts.Add(outcome.IsSuccess
          ? new MemberReceipt(id, outcome.Value.Status, outcome.Value.Reason)
          : new MemberReceipt(id, AgentStatus.Failed, null));
    }

    receipts.AddRange(startReceipts.Select(line =>
        new MemberReceipt(AgentId.NewId(), AgentStatus.Failed, null)));
    bool allCompleted = receipts.All(receipt => receipt.Status is AgentStatus.Completed);
    SpawnGraphOutcome graphOutcome = new(allCompleted, receipts);
    return allCompleted
        ? Result.Success(graphOutcome)
        : Result.Failure<SpawnGraphOutcome>(new DomainError("JoinFailed", graphOutcome.Render()));
  }
}

/// <summary>One spawn graph request: children described in a single call; the join policy
///     decides fail-fast vs collect-all.</summary>
public sealed record SpawnGraphRequest(string Label, IReadOnlyList<SpawnRequest> Children, JoinPolicy Join);

public sealed record JoinPolicy(bool FailFast = false);

public sealed record MemberReceipt(AgentId ChildId, AgentStatus Status, AgentFailureReason? Reason);

public sealed record SpawnGraphOutcome(bool AllCompleted, IReadOnlyList<MemberReceipt> Receipts)
{
  public string Render()
      => "graph " + (AllCompleted ? "completed" : "failed") + "; receipts: "
          + string.Join("; ", Receipts.Select(receipt =>
              receipt.ChildId + "=" + receipt.Status.ToString().ToUpperInvariant()
                  + (receipt.Reason is null ? "" : "(" + receipt.Reason.Value + ")")));
}
