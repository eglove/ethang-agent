namespace eThangAgent.ToolDomain;

/// <summary>The completion gate's ADVISORY surface for <c>git_commit</c> (issue #283).
///     <para>WHY ADVISORY HERE AND STRICT AT THE PLAN: committing work in progress is
///     legitimate - a checkpoint commit mid-task is normal practice - so a hard refusal
///     at the commit would push the model toward <c>--no-verify</c> and other bypasses,
///     which is a worse outcome than a stale gate. The strict attach point is
///     <c>plan.set-status Completed</c>, where 'the work is finished' is actually being
///     claimed.</para>
///     <para>Returns null when there is nothing to say (no gate configured, or the gate
///     is fresh), so a healthy commit's output is byte-identical to before.</para></summary>
public interface ICommitCompletionNotice
{
  /// <summary>An advisory line naming a stale or failing gate, or null.</summary>
  Task<string?> NoticeAsync(CancellationToken ct = default);
}
