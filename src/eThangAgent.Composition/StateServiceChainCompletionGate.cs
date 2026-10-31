using eThangAgent.AgentDomain;
using eThangAgent.StateDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition;

/// <summary>Composition adapter: answers the Agent Domain's chain-completion question from the
///     Tool Domain's <see cref="CompletionGate"/> and the State Domain's three keys
///     (<c>goal/check</c>, <c>goal/lastcheck</c>, <c>goal/declined</c>) (issue #304 D1).
///     <para>WHY THIS IS A SECOND ADAPTER AND NOT A REUSE OF THE PLAN ONE: the two live in
///     different domains. <c>StateServicePlanCompletionGate</c> implements the Plan Domain's
///     port, and the Agent Domain must not reference the Plan Domain any more than the reverse.
///     What is shared is the GATE - one evaluator, one command set, one answer - which is what
///     D1 actually requires; the two ports are how each domain asks without depending on the
///     other.</para>
///     <para>THE THREE KEYS ARE READ THE SAME WAY THE PLAN ADAPTER READS THEM, deliberately: a
///     declared gate wins over a stale decline, and a workspace that declined is a DIFFERENT
///     state from one nobody asked (#285 D1) even though neither blocks.</para></summary>
internal sealed class StateServiceChainCompletionGate(CompletionGate gate, IStateService state,
    Func<string> workspaceRoot) : IChainCompletionGate
{
  /// <summary>Answers the question. An unset or declined gate NEVER blocks (D2, #285 D1): the
  ///     harness has no business demanding a check the project never wrote.</summary>
  /// <param name="ct">Cancellation.</param>
  public async Task<(bool Allowed, string Message)> EvaluateAsync(CancellationToken ct = default)
  {
    bool configured = (await state.GetAsync(CompletionGate.GoalCheckKey, ct).ConfigureAwait(false)).IsSuccess;
    bool declined = !configured
        && (await state.GetAsync(CompletionGate.DeclinedKey, ct).ConfigureAwait(false)).IsSuccess;
    string? lastCheck = (await state.GetAsync(CompletionGate.LastCheckKey, ct).ConfigureAwait(false)) is { IsSuccess: true } stored
        ? stored.Value
        : null;

    CompletionGateVerdict verdict = await gate
        .EvaluateAsync(workspaceRoot(), configured, lastCheck, declined, ct)
        .ConfigureAwait(false);
    return (!verdict.BlocksCompletion, verdict.Message);
  }
}
