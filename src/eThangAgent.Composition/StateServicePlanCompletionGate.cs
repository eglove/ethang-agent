using eThangAgent.PlanDomain;
using eThangAgent.ToolDomain.Verification;

namespace eThangAgent.Composition;

/// <summary>Composition adapter: answers the Plan Domain's completion-gate question from
///     the Tool Domain's <see cref="CompletionGate"/>.
///     <para>The Plan Domain asks the question and the Tool Domain owns the answer; only the
///     composition root may know both.</para></summary>
internal sealed class StateServicePlanCompletionGate(CompletionGate gate) : IPlanCompletionGate
{
  private readonly CompletionGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));

  public async Task<(bool Allowed, string Message)> EvaluateAsync(CancellationToken ct = default)
  {
    CompletionVerdict verdict = await _gate.EvaluateAsync(ct).ConfigureAwait(false);
    return (!verdict.BlocksCompletion, verdict.Message);
  }
}
