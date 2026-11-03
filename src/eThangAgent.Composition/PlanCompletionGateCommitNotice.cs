using eThangAgent.PlanDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition;

/// <summary>Composition adapter: renders the completion gate's verdict as the ADVISORY
///     line <c>git_commit</c> appends. The same gate that refuses a plan completion speaks
///     here, so the two attach points can never disagree about the session's state - only
///     about how loudly they say it.</summary>
internal sealed class PlanCompletionGateCommitNotice(IPlanCompletionGate gate) : ICommitCompletionNotice
{
  private readonly IPlanCompletionGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));

  public async Task<string?> NoticeAsync(CancellationToken ct = default)
  {
    (bool allowed, string message) = await _gate.EvaluateAsync(ct).ConfigureAwait(false);
    return allowed ? null : "[warning] " + message + " (the commit proceeded.)";
  }
}
