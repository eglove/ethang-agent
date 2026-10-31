using eThangAgent.PlanDomain;
using eThangAgent.StateDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition;

/// <summary>Composition adapter: answers the Plan Domain's completion-gate question from
///     the Tool Domain's <see cref="CompletionGate"/> and the State Domain's two keys
///     (<c>goal/check</c> for the project's commands, <c>goal/lastcheck</c> for the last
///     run's outcome).
///     <para>The three pieces live in three domains on purpose: the Plan Domain asks the
///     question, the State Domain owns the durable facts, and the Tool Domain owns the
///     freshness comparison. Only the composition root may know all three.</para></summary>
internal sealed class StateServicePlanCompletionGate(CompletionGate gate, IStateService state,
    Func<string> workspaceRoot) : IPlanCompletionGate
{
  public async Task<(bool Allowed, string Message)> EvaluateAsync(CancellationToken ct = default)
  {
    // An unset goal/check is a workspace that declares no gate. It must NEVER block: the
    // harness has no business demanding a check the project never wrote, and a hard
    // requirement here would make the feature unusable on every repo but this one.
    bool configured = (await state.GetAsync(CompletionGate.GoalCheckKey, ct).ConfigureAwait(false)).IsSuccess;
    // The decline is a SEPARATE key (issue #285 D1), so a workspace whose user said no is
    // distinguishable from one nobody ever asked. Read only when no gate is declared: a
    // declared gate wins, and a stale decline left behind by an earlier choice must never
    // suppress a gate the user has since configured.
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
