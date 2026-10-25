using eThangAgent.PlanDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition;

/// <summary>Composition adapter: renders the completion gate's verdict as the ADVISORY
///     line <c>git_commit</c> appends (issue #283). The same gate that refuses a plan
///     completion speaks here, so the two attach points can never disagree about the
///     workspace's state - only about how loudly they say it.</summary>
internal sealed class PlanCompletionGateCommitNotice(IPlanCompletionGate gate) : ICommitCompletionNotice
{
  public async Task<string?> NoticeAsync(CancellationToken ct = default)
  {
    (bool allowed, string message) = await gate.EvaluateAsync(ct).ConfigureAwait(false);
    return allowed ? null : "[warning] " + message + " (the commit proceeded.)";
  }
}
