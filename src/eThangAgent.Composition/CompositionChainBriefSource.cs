using eThangAgent.AgentDomain;
using eThangAgent.PlanDomain;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;
using eThangAgent.StateDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition;

/// <summary>Implements the Agent Domain's brief port over the real stores (issue #302).
///     <para>WHY HERE AND NOT IN THE AGENT DOMAIN: the facts live in the Plan Domain and the
///     chain record, and the Agent Domain references neither. The composition root is the one
///     place allowed to know both.</para>
///     <para>EVERY READ IS A READ. Nothing here writes, and nothing here renders: the brief's
///     text is the Agent Domain's, so a second renderer cannot drift from the first.</para></summary>
internal sealed class CompositionChainBriefSource(ChainService chains, PlanService plans,
    ISkillCatalog skills, IGitQueryAccess git, IWorkspaceContext workspace) : IChainBriefSource
{
  /// <summary>The embedded skill that carries the reviewer's rubric (issue #217). Named here
  ///     rather than assumed present: until #217 ships, a review brief says plainly that no
  ///     rubric is installed instead of inventing one.</summary>
  private const string ReviewRubricSkill = "reviewing-plans";

  private readonly ChainService _chains = chains ?? throw new ArgumentNullException(nameof(chains));
  private readonly PlanService _plans = plans ?? throw new ArgumentNullException(nameof(plans));
  private readonly ISkillCatalog _skills = skills ?? throw new ArgumentNullException(nameof(skills));
  private readonly IGitQueryAccess _git = git ?? throw new ArgumentNullException(nameof(git));
  private readonly IWorkspaceContext _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));

  /// <inheritdoc />
  public async Task<Result<ChainBriefChain>> GetChainAsync(int chainId, int position,
      CancellationToken ct = default)
  {
    Result<Chain> chain = await _chains.GetAsync(chainId, ct).ConfigureAwait(false);
    if (!chain.IsSuccess)
    {
      return Result.Failure<ChainBriefChain>(chain.Error);
    }

    Result<ChainBriefReconciliation?> reconciliation = await ReconciliationAsync(chain.Value, position, ct)
        .ConfigureAwait(false);
    return reconciliation.IsSuccess
      ? Result.Success(new ChainBriefChain(chain.Value.Id, chain.Value.Title, chain.Value.PlanId,
          Predecessors(chain.Value), RoleOf(chain.Value, position), GroupPositions(chain.Value, position),
          reconciliation.Value))
      : Result.Failure<ChainBriefChain>(reconciliation.Error);
  }

  /// <summary>What the step at <paramref name="position"/> must merge, when it is the RECONCILER
  ///     of a group that already ran, or null for every other step.
  ///     <para>THE GROUP IS THE ONE AT THE PREVIOUS POSITION (D7): a reconciler is the step
  ///     immediately after its group's last member, so the group it merges is the one owning the
  ///     position before it.</para>
  ///     <para>THE CHECKOUT IS READ HERE because this is the one place that knows both the chain
  ///     record and the workspace directory, and a failed read is carried as a failed read - an
  ///     empty dirty list would tell the reconciler the checkout is clean when nobody looked.
  ///     </para></summary>
  /// <param name="chain">The chain the step belongs to.</param>
  /// <param name="position">The 1-based position of the step this brief is for.</param>
  /// <param name="ct">Cancellation.</param>
  private async Task<Result<ChainBriefReconciliation?>> ReconciliationAsync(Chain chain, int position,
      CancellationToken ct)
  {
    // THE RECONCILER IS THE STEP IMMEDIATELY AFTER A GROUP'S LAST MEMBER, and it stands OUTSIDE
    // that group - chain creation requires exactly that. Reading the group at the position before
    // this one is therefore right only when that position is the group's LAST member: asked of
    // any earlier member it names the group this step does not reconcile, and the brief would
    // claim a merge that is not this step's to perform.
    if (chain.GroupAt(position - 1) is not { } group || chain.GroupAt(position) is not null)
    {
      return Result.Success<ChainBriefReconciliation?>(null);
    }

    Result<CheckoutState> checkout = await CheckoutAsync(ct).ConfigureAwait(false);
    return checkout.IsSuccess
      ? Result.Success<ChainBriefReconciliation?>(new ChainBriefReconciliation(
          [.. chain.GroupMembers(group)
              .Where(member => member.WorktreePath is not null && member.WorktreeBranch is not null)
              .Select(member => new ChainBriefBranch(member.Position, member.WorktreeBranch!, member.WorktreePath!))],
          checkout.Value.DirtyFiles,
          // A MEMBER IS JUDGED BY WHETHER IT SETTLED, not by whether it is Done: a member still
          // running has not failed, and naming it as one would tell the reconciler to leave its
          // branch out of a merge it has not written yet.
          [.. chain.GroupMembers(group)
              .Where(member => member.Status is not (ChainStepStatus.Done or ChainStepStatus.Running))
              .Select(member => member.Position)],
          checkout.Value.Readable))
      : Result.Failure<ChainBriefReconciliation?>(checkout.Error);
  }

  /// <summary>The workspace checkout's uncommitted files, or the fact that it could not be read.
  ///     <para>AN UNREADABLE CHECKOUT IS A RESULT, NOT A FAILURE: the reconciler is told to check
  ///     the checkout itself, which is a usable instruction, while a failed brief would leave it
  ///     with no branches at all.</para></summary>
  /// <param name="ct">Cancellation.</param>
  private async Task<Result<CheckoutState>> CheckoutAsync(CancellationToken ct)
  {
    Result<GitStatus> status = await _git.GetStatusAsync(_workspace.WorkspaceRoot, ct).ConfigureAwait(false);
    return status.IsSuccess
      ? Result.Success(new CheckoutState(true,
          [.. status.Value.Staged.Select(entry => entry.Path)
              .Concat(status.Value.Unstaged.Select(entry => entry.Path))
              .Concat(status.Value.Untracked)
              .Distinct(StringComparer.Ordinal)]))
      : Result.Success(new CheckoutState(false, []));
  }

  /// <summary>The checkout as the brief reports it: its dirty files, and whether it could be read
  ///     at all.</summary>
  /// <param name="Readable">False when the status query failed.</param>
  /// <param name="DirtyFiles">The uncommitted files, empty when the checkout is clean.</param>
  private sealed record CheckoutState(bool Readable, IReadOnlyList<string> DirtyFiles);

  /// <summary>Whether the reviewer's rubric skill is installed (issue #306). The catalog is the
  ///     same one this session's listing is built from, so the brief and the skill listing
  ///     cannot disagree about whether a rubric exists.</summary>
  public async Task<Result<bool>> GetReviewRubricAvailableAsync(CancellationToken ct = default)
  {
    Result<IReadOnlyList<SkillDefinition>> listed = await _skills.ListAsync(ct).ConfigureAwait(false);
    return listed.IsSuccess
      ? Result.Success(listed.Value.Any(skill =>
          string.Equals(skill.Name, ReviewRubricSkill, StringComparison.OrdinalIgnoreCase)))
      : Result.Failure<bool>(listed.Error);
  }

  /// <summary>The role of the chain step at <paramref name="position"/>, or null when the chain
  ///     holds no step there.
  ///     <para>THE RECORD IS THE SOURCE, not the assignment: a resumed or restarted launch reads
  ///     the row, and the row is what #218's separation rule reads too.</para></summary>
  private static ChainStepRole? RoleOf(Chain chain, int position) =>
      chain.Steps.FirstOrDefault(step => step.Position == position)?.Role;

  /// <summary>The positions of the group the step at <paramref name="position"/> belongs to,
  ///     including that step, or an empty list when the step runs alone or the chain holds no
  ///     step there.</summary>
  private static IReadOnlyList<int> GroupPositions(Chain chain, int position) =>
      chain.GroupAt(position) is { } group
        ? [.. chain.GroupMembers(group).Select(step => step.Position)]
        : [];

  /// <inheritdoc />
  public async Task<Result<ChainBriefPlan>> GetPlanAsync(int planId, CancellationToken ct = default)
  {
    Result<Plan> plan = await _plans.GetAsync(planId, ct).ConfigureAwait(false);
    return plan.IsSuccess
      ? Result.Success(new ChainBriefPlan(plan.Value.Id, plan.Value.Version, plan.Value.Goal,
          [.. plan.Value.Steps.Select(step => new ChainBriefPlanStep(step.Position, step.Title, step.Detail))]))
      : Result.Failure<ChainBriefPlan>(plan.Error);
  }

  /// <summary>The chain steps that already ran, in position order. A step that is still
  ///     Pending or Running is not a predecessor: it has concluded nothing yet, and listing it
  ///     would tell this session about work that is happening rather than work that happened.</summary>
  private static IReadOnlyList<ChainBriefPredecessor> Predecessors(Chain chain) =>
      [.. chain.Steps
          .Where(step => step.Status is ChainStepStatus.Done or ChainStepStatus.Failed
              or ChainStepStatus.Skipped or ChainStepStatus.Parked)
          .OrderBy(step => step.Position)
          .Select(step => new ChainBriefPredecessor(step.Position, step.Status, step.Outcome))];
}
