namespace eThangAgent.AgentDomain;

/// <summary>One plan step as the brief needs it. Deliberately NOT <c>PlanStep</c>: the Agent
///     Domain must not reference the Plan Domain, and the brief needs only these four facts.
///     <para><paramref name="Detail"/> is nullable because a plan step's detail is optional
///     (74 of 588 steps in this workspace carry none), and the brief must render that absence
///     as an absence rather than as an empty section.</para></summary>
/// <param name="Position">1-based position in the plan.</param>
/// <param name="Title">The step's title.</param>
/// <param name="Detail">The step's optional detail text.</param>
public sealed record ChainBriefPlanStep(int Position, string Title, string? Detail);

/// <summary>One chain step that has already run, as the brief reports it (issue #302
///     amendment): a step must not re-derive what the ledger already establishes, so the
///     brief names its predecessors and what each concluded.</summary>
/// <param name="Position">1-based position in the chain.</param>
/// <param name="Status">The step's settled status.</param>
/// <param name="Outcome">What the step recorded, when it recorded anything.</param>
public sealed record ChainBriefPredecessor(int Position, ChainStepStatus Status, string? Outcome);

/// <summary>One step running at the same moment as this one, as a member of the same group
///     needs it. A sibling is NOT a predecessor: its outcome does not exist yet, so the brief
///     names it as work happening beside this session rather than as work that concluded.
///     </summary>
/// <param name="Position">1-based chain step position of the sibling.</param>
/// <param name="Title">The sibling's title.</param>
public sealed record ChainBriefSibling(int Position, string Title);

/// <summary>One group member's branch and the worktree it was committed in, as the reconciler
///     needs them (D7). The BRANCH IS STORED, never derived from the worktree name: a derived
///     branch is a second source of truth that can disagree with what git checked out, and the
///     reconciler merges by branch.</summary>
/// <param name="Position">1-based chain step position of the member.</param>
/// <param name="Branch">The branch the member's work is committed on.</param>
/// <param name="WorktreePath">The worktree directory that branch was checked out in.</param>
public sealed record ChainBriefBranch(int Position, string Branch, string WorktreePath);

/// <summary>What a RECONCILER must know before it merges (D7/D14): the branches the group left
///     behind, the state of the workspace checkout it merges into, and which members did not
///     succeed.
///     <para>THE CHECKOUT'S STATE IS A FACT, NOT A REFUSAL: git is the model's to handle, so the
///     reconciler is told what is there and decides. <paramref name="DirtyFiles"/> empty means
///     the checkout is clean; a read that FAILED is a different fact and is carried by
///     <paramref name="CheckoutReadable"/> rather than by an empty list, because 'nothing is
///     there' and 'nobody could look' are not the same answer.</para></summary>
/// <param name="Branches">The members' branches, in position order.</param>
/// <param name="DirtyFiles">The checkout's uncommitted files, empty when it is clean.</param>
/// <param name="FailedPositions">The members whose work did not land, in position order.</param>
/// <param name="CheckoutReadable">False when the checkout's status could not be read at all.
///     Defaults to true: a caller that read the status successfully says nothing about it.
/// </param>
public sealed record ChainBriefReconciliation(
    IReadOnlyList<ChainBriefBranch> Branches,
    IReadOnlyList<string> DirtyFiles,
    IReadOnlyList<int> FailedPositions,
    bool CheckoutReadable = true);
/// <summary>Everything the brief renders, gathered by <see cref="IChainBriefSource"/> and
///     turned into text by <see cref="ChainBriefRenderer"/> (issue #302 D1).
///     <para>WHY FACTS AND NOT THE RECORDS: the renderer must be a PURE function so the same
///     chain and plan always produce the same brief, which is what lets a test pin the text
///     and what stops the brief from drifting between a first launch and a resume. Gathering
///     the facts is I/O; rendering them is not.</para></summary>
/// <param name="ChainId">The chain the step belongs to.</param>
/// <param name="ChainTitle">The chain's title, which also derives its ledger slug.</param>
/// <param name="PlanId">The plan the chain executes.</param>
/// <param name="PlanVersion">The plan version the chain STARTED at (D8: a later revision is
///     visible but never retroactive).</param>
/// <param name="PlanGoal">The plan's goal — the model-authored free-text section (D3).</param>
/// <param name="AssignedPosition">The 1-based chain step position this session owns.</param>
/// <param name="PlanSteps">Every plan step, so the brief can render the assigned one and
///     refuse when it is gone.</param>
/// <param name="Predecessors">The chain steps that already ran, in position order.</param>

/// <param name="LedgerKey">Where the chain's ledger lives, already resolved from the slug.</param>
/// <param name="Role">The role assigned to this step's session (issue #306): implement or
///     review. The brief branches on it, because a reviewer told to mark its step done is a
///     reviewer that will finish the work instead of judging it. Null means implement.</param>
/// <param name="RefineRubricAvailable">Whether the <c>writing-plans</c> skill (#217) is
///     installed in this session's catalog. A reviewer must be told which rubric to apply, and
///     the honest answer when there is none is to say so rather than to invent one.</param>
/// <param name="IsFinal">Whether this brief is for the FINAL agent: the chain is complete, so
///     there is no assigned step and no completion contract. The final agent summarizes the
///     work done and hands control back to the user.</param>
public sealed record ChainBriefFacts(
    int ChainId,
    string ChainTitle,
    int PlanId,
    int PlanVersion,
    string PlanGoal,
    int AssignedPosition,
    IReadOnlyList<ChainBriefPlanStep> PlanSteps,
    IReadOnlyList<ChainBriefPredecessor> Predecessors,
    string LedgerKey,
    ChainStepRole? Role = null,
    bool RefineRubricAvailable = false,
    bool IsFinal = false)
{
  /// <summary>The plan step this session was assigned, or null when the plan no longer has
  ///     one at that position. Chains bind to plan steps BY POSITION, and
  ///     <c>plan.remove-step</c> renumbers the tail, so a revised plan can leave a chain
  ///     step pointing at work that no longer exists — the defect the refusal exists for.</summary>
  public ChainBriefPlanStep? AssignedStep =>
      PlanSteps.FirstOrDefault(step => step.Position == AssignedPosition);

  /// <summary>The step's role, never null: an absent role means 'implement', the same default
  ///     the chain record itself applies to a step that names none.</summary>
  [System.Text.Json.Serialization.JsonIgnore]
  public ChainStepRole EffectiveRole => Role ?? ChainStepRole.Implement;

  /// <summary>True when this step's session JUDGES work rather than doing it (issue #306).
  ///     <para>THE ONE BRANCH THE BRIEF MAKES ON ROLE. Everything else about a step — its
  ///     position, its title, its detail, the plan's goal, the predecessors, the gate — is the
  ///     same fact whether a session is implementing or reviewing. What differs is what
  ///     "finished" means, and that is the whole of the difference.</para></summary>
  public bool IsRefine => EffectiveRole.IsRefine;

  /// <summary>The other members of this step's group, in position order, so a member knows
  ///     which work is happening beside it and must neither change nor wait on it. Empty for a
  ///     step that runs alone.</summary>
  public IReadOnlyList<ChainBriefSibling> Siblings { get; init; } = [];

  /// <summary>True when this step runs as one of a group, so its completion starts nothing on
  ///     its own and its work must be committed before it completes.</summary>
  public bool IsGroupMember => Siblings.Count > 0;

  /// <summary>What this step must merge, when it is the RECONCILER of a group that already ran:
  ///     the members' branches, the state of the workspace checkout, and the members that did
  ///     not succeed. Null for every other step, which merges nothing.</summary>
  public ChainBriefReconciliation? Reconciliation { get; init; }
}

/// <summary>The rendered brief (issue #302): the first message a chain step's session
///     receives.</summary>
/// <param name="Content">The brief text, rendered and ready to seed.</param>
public sealed record ChainBrief(string Content);
