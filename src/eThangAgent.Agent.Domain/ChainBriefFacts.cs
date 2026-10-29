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
/// <param name="GateCommands">The workspace's <c>goal/check</c> commands; empty when unset.</param>
/// <param name="LedgerKey">Where the chain's ledger lives, already resolved from the slug.</param>
public sealed record ChainBriefFacts(
    int ChainId,
    string ChainTitle,
    int PlanId,
    int PlanVersion,
    string PlanGoal,
    int AssignedPosition,
    IReadOnlyList<ChainBriefPlanStep> PlanSteps,
    IReadOnlyList<ChainBriefPredecessor> Predecessors,
    IReadOnlyList<string> GateCommands,
    string LedgerKey)
{
  /// <summary>The plan step this session was assigned, or null when the plan no longer has
  ///     one at that position. Chains bind to plan steps BY POSITION, and
  ///     <c>plan.remove-step</c> renumbers the tail, so a revised plan can leave a chain
  ///     step pointing at work that no longer exists — the defect the refusal exists for.</summary>
  public ChainBriefPlanStep? AssignedStep =>
      PlanSteps.FirstOrDefault(step => step.Position == AssignedPosition);
}

/// <summary>The rendered brief (issue #302): the first message a chain step's session
///     receives.</summary>
/// <param name="Content">The brief text, rendered and ready to seed.</param>
public sealed record ChainBrief(string Content);
