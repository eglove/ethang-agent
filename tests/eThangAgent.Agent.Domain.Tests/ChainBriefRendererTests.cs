using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Domain.Tests;

/// <summary>The chain brief's rendered text (issue #302). These pin the TEXT, not the shape:
///     the brief is what a session reads, so a test that only checked 'it rendered something'
///     would pass while the brief said the wrong step.</summary>
public sealed class ChainBriefRendererTests
{
  private static ChainBriefFacts Facts(
      int assigned = 2,
      IReadOnlyList<ChainBriefPlanStep>? steps = null,
      IReadOnlyList<ChainBriefPredecessor>? predecessors = null,
      string goal = "Make the dashboard tell the truth about what it did.",
      ChainStepRole? role = null,
      bool rubric = false,
      IReadOnlyList<ChainBriefSibling>? siblings = null) =>
      new(ChainId: 7,
          ChainTitle: "2026-08-24-dashboard-truth",
          PlanId: 104,
          PlanVersion: 3,
          PlanGoal: goal,
          AssignedPosition: assigned,
          PlanSteps: steps ?? [
            new ChainBriefPlanStep(1, "Honest status line", "Rewrite the placed/refused summary."),
            new ChainBriefPlanStep(2, "Real demo broker", null),
            new ChainBriefPlanStep(3, "Docs", "Update EXECUTION.md."),
          ],
          Predecessors: predecessors ?? [],
          LedgerKey: "sci.dashboard-truth/ledger",
          Role: role,
          ReviewRubricAvailable: rubric)
      {
        Siblings = siblings ?? [],
      };

  /// <summary>The reconciler's facts: a step's ordinary facts plus what it must merge. Kept as a
  ///     separate builder so the shared one stays within the parameter bound.</summary>
  private static ChainBriefFacts ReconcilerFacts(ChainBriefReconciliation reconciliation) =>
      Facts() with { Reconciliation = reconciliation };

  [Fact]
  public void Render_NamesTheAssignedStepThePlanAndTheChain()
  {
    ChainBrief brief = ChainBriefRenderer.Render(Facts()).Value!;

    Assert.StartsWith(ChainBriefRenderer.Prefix, brief.Content, StringComparison.Ordinal);
    Assert.Contains("You are step 2 of chain #7, executing plan #104.", brief.Content, StringComparison.Ordinal);
    Assert.Contains("Position 2: Real demo broker", brief.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_CarriesThePlanGoalVerbatim()
  {
    const string goal = "Traps I already hit: the envelope refuses 4 of 6 lines.\n"
        + "What I rejected: retrying the broker call.";

    ChainBrief brief = ChainBriefRenderer.Render(Facts(goal: goal)).Value!;

    // Verbatim: the model-authored half is the one thing a renderer cannot invent.
    Assert.Contains(goal, brief.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_NamesTheLedgerAndTellsTheSessionToReadItBeforeStarting()
  {
    ChainBrief brief = ChainBriefRenderer.Render(Facts()).Value!;

    Assert.Contains("sci.dashboard-truth/ledger", brief.Content, StringComparison.Ordinal);
    Assert.Contains("Read before you start", brief.Content, StringComparison.Ordinal);
    Assert.Contains("CHAIN scope", brief.Content, StringComparison.Ordinal);
    // The amendment's instruction: record as you work, not only at the end.
    Assert.Contains("AS YOU WORK", brief.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_StatesTheCompletionContractNamingTheOneStepItMayMark()
  {
    ChainBrief brief = ChainBriefRenderer.Render(Facts()).Value!;

    Assert.Contains("plan.update-step id=104 position=2 status=Done", brief.Content, StringComparison.Ordinal);
    Assert.Contains("ONLY step you may mark done", brief.Content, StringComparison.Ordinal);
    Assert.Contains("starts the next one", brief.Content, StringComparison.Ordinal);
  }

  /// <summary>The workspace declares no command set, so the brief names the DECISION the session
  ///     must make instead of a command list, and states the freshness rule the gate applies.
  ///     </summary>
  [Fact]
  public void Render_NamesWhatBacksADoneClaim_WithoutNamingACommandSet()
  {
    ChainBrief brief = ChainBriefRenderer.Render(Facts()).Value!;

    Assert.Contains("## What backs your done-claim", brief.Content, StringComparison.Ordinal);
    Assert.Contains("exits 0", brief.Content, StringComparison.Ordinal);
    Assert.Contains("after your last change", brief.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("goal/check", brief.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("state.checkgoal", brief.Content, StringComparison.Ordinal);
  }

  /// <summary>Issue #315: the brief instructs the session to SEARCH for refutations before it
  ///     starts, and names the workspace-visible namespace they live in. Without this the shared
  ///     records would exist and nobody would look, which is the half of the feature that makes
  ///     the other half worth anything.</summary>
  [Fact]
  public void Render_TellsTheSessionToSearchForRefutationsBeforeStarting()
  {
    ChainBrief brief = ChainBriefRenderer.Render(Facts()).Value!;

    Assert.Contains("## Search for refutations before you start", brief.Content, StringComparison.Ordinal);
    Assert.Contains("`refutations/`", brief.Content, StringComparison.Ordinal);
    Assert.Contains("state.find", brief.Content, StringComparison.Ordinal);
    Assert.Contains("WORKSPACE scope, not chain scope", brief.Content, StringComparison.Ordinal);
    // And it says the session does not write that namespace by hand: the tool does, in the same
    // call that records the verdict.
    Assert.Contains("You do not write to that namespace yourself", brief.Content, StringComparison.Ordinal);
    Assert.Contains("verdict=refuted", brief.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_ListsPredecessorsAndWhatEachConcluded()
  {
    ChainBrief brief = ChainBriefRenderer.Render(Facts(predecessors: [
      new ChainBriefPredecessor(1, ChainStepStatus.Done, "status line rewritten"),
      new ChainBriefPredecessor(2, ChainStepStatus.Failed, null),
    ])).Value!;

    Assert.Contains("- Step 1 — Done: status line rewritten", brief.Content, StringComparison.Ordinal);
    Assert.Contains("- Step 2 — Failed: (no outcome recorded)", brief.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_WithNoPredecessors_OmitsTheSectionEntirely()
  {
    ChainBrief brief = ChainBriefRenderer.Render(Facts()).Value!;

    Assert.DoesNotContain("Steps that already ran", brief.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_IsPure_TheSameFactsRenderByteIdentically()
  {
    ChainBriefFacts facts = Facts();

    Assert.Equal(ChainBriefRenderer.Render(facts).Value!.Content,
        ChainBriefRenderer.Render(facts).Value!.Content, StringComparer.Ordinal);
  }

  [Fact]
  public void Render_WhenThePlanStepIsGone_RefusesWithANamedCode()
  {
    // The plan was revised and its step 2 removed: the chain step now points at nothing.
    Result<ChainBrief> brief = ChainBriefRenderer.Render(Facts(assigned: 2, steps: [
      new ChainBriefPlanStep(1, "Only step left", null),
    ]));

    Assert.False(brief.IsSuccess);
    Assert.Equal("ChainStepMissing", brief.Error.Code);
    Assert.Contains("re-create the chain", brief.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_ForAReviewStep_TellsItToJudgeRatherThanToFinishTheWork()
  {
    ChainBrief brief = ChainBriefRenderer.Render(Facts(role: ChainStepRole.Review("vendor/reviewer"))).Value!;

    // The defect this issue exists for: an implementer's contract handed to a reviewer.
    Assert.Contains("You are the REVIEWER for this step", brief.Content, StringComparison.Ordinal);
    Assert.Contains("JUDGE the", brief.Content, StringComparison.Ordinal);
    Assert.Contains("Do not change the workspace", brief.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("## How to finish\n\nMark step 2 of plan #104 done:", brief.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_ForAReviewStep_StillNamesTheOneStepItMayMarkDone()
  {
    ChainBrief brief = ChainBriefRenderer.Render(Facts(role: ChainStepRole.Review())).Value!;

    // A reviewer still has to COMPLETE its step - the ownership gate and the harness's advance
    // both read that write. What changes is what the step is for, not whether it finishes.
    Assert.Contains("plan.update-step id=104 position=2 status=Done", brief.Content, StringComparison.Ordinal);
    Assert.Contains("ONLY step you may mark done", brief.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_ForAReviewStep_StatesTheVerdictShapeAndTheCitationRule()
  {
    ChainBrief brief = ChainBriefRenderer.Render(Facts(role: ChainStepRole.Review())).Value!;

    Assert.Contains("verdict: approve | deny | approve-with-conditions", brief.Content, StringComparison.Ordinal);
    Assert.Contains("citing a requirement id or a step position", brief.Content, StringComparison.Ordinal);
    Assert.Contains("A finding that cites nothing is not a finding", brief.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_ForAReviewStep_WithTheRubricSkill_NamesIt()
  {
    ChainBrief brief = ChainBriefRenderer.Render(
        Facts(role: ChainStepRole.Review(), rubric: true)).Value!;

    Assert.Contains("Load the `reviewing-plans` skill", brief.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("no rubric to apply", brief.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_ForAReviewStep_WithoutTheRubricSkill_SaysSoRatherThanInventingOne()
  {
    ChainBrief brief = ChainBriefRenderer.Render(
        Facts(role: ChainStepRole.Review(), rubric: false)).Value!;

    // An invented rubric is a verdict nobody can compare; an absent one is visibly absent.
    Assert.Contains("No `reviewing-plans` skill is installed", brief.Content, StringComparison.Ordinal);
    Assert.Contains("rubric was available", brief.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("Load the `reviewing-plans` skill", brief.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_ForAnImplementStep_IsUnchangedByTheRoleBranch()
  {
    // The criterion that protects every existing chain: an implement step's brief is what it
    // was before the branch existed, and it never mentions reviewing.
    string byDefault = ChainBriefRenderer.Render(Facts()).Value!.Content;
    string explicitImplement = ChainBriefRenderer.Render(
        Facts(role: ChainStepRole.Implement)).Value!.Content;

    Assert.Equal(byDefault, explicitImplement, StringComparer.Ordinal);
    Assert.DoesNotContain("REVIEWER", byDefault, StringComparison.Ordinal);
    Assert.Contains("## How to finish", byDefault, StringComparison.Ordinal);
    Assert.Contains("plan.update-step id=104 position=2 status=Done", byDefault, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_RefusalNamesThePositionAndThePlanItExpected()
  {
    Result<ChainBrief> brief = ChainBriefRenderer.Render(Facts(assigned: 9));

    Assert.False(brief.IsSuccess);
    Assert.Equal("ChainStepMissing", brief.Error.Code);
    Assert.Contains("step 9", brief.Error.Message, StringComparison.Ordinal);
    Assert.Contains("plan #104", brief.Error.Message, StringComparison.Ordinal);
  }

  /// <summary>A group member's brief names the steps running BESIDE it (D6): their outcomes do
  ///     not exist yet, so the brief must say so rather than list them as predecessors.
  ///     </summary>
  [Fact]
  public void Render_ForAGroupMember_ListsTheStepsRunningBesideIt()
  {
    ChainBrief brief = ChainBriefRenderer.Render(Facts(siblings: [
      new ChainBriefSibling(2, "Parse the schema"),
      new ChainBriefSibling(3, "Write the reader"),
    ])).Value!;

    Assert.Contains(
        """
        ## Steps running BESIDE you

        These steps of the same chain are running at the same moment as yours, each in its own
        git worktree. Their outcomes do not exist yet. Do not change work that belongs to them,
        and do not wait for them: the harness merges the branches when every member has finished.

        - Step 2 — Parse the schema
        - Step 3 — Write the reader
        """,
        brief.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_WithNoSiblings_OmitsTheSectionEntirely()
  {
    ChainBrief brief = ChainBriefRenderer.Render(Facts()).Value!;

    Assert.DoesNotContain("Steps running BESIDE you", brief.Content, StringComparison.Ordinal);
  }

  /// <summary>Completing one member starts nothing: the group is a barrier, and the harness
  ///     starts what follows only once every member has finished. A member told the sequential
  ///     rule would believe its own completion advanced the chain.</summary>
  [Fact]
  public void Render_ForAGroupMember_DoesNotClaimItsCompletionStartsTheNextStep()
  {
    ChainBrief brief = ChainBriefRenderer.Render(Facts(siblings: [
      new ChainBriefSibling(3, "Write the reader"),
    ])).Value!;

    Assert.DoesNotContain("Completing your step is what starts the next one", brief.Content, StringComparison.Ordinal);
    Assert.Contains("Completing your step starts nothing on its own", brief.Content, StringComparison.Ordinal);
    Assert.Contains("the harness starts what follows only once every member has finished",
        brief.Content, StringComparison.Ordinal);
  }

  /// <summary>The sequential sentence is pinned here so the member branch cannot quietly change
  ///     it: every chain that exists today reads this text.</summary>
  [Fact]
  public void Render_ForASequentialStep_StillClaimsItsCompletionStartsTheNextStep()
  {
    ChainBrief brief = ChainBriefRenderer.Render(Facts()).Value!;

    Assert.Contains("Completing your step is what starts the next one", brief.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("Completing your step starts nothing on its own", brief.Content, StringComparison.Ordinal);
  }

  /// <summary>A member's work must be committed: the harness merges its branch, and an
  ///     uncommitted worktree refuses the completion. The brief names the commands because a
  ///     session that has to guess them guesses wrong.</summary>
  [Fact]
  public void Render_ForAGroupMember_RequiresACommitAndNamesTheCommands()
  {
    ChainBrief brief = ChainBriefRenderer.Render(Facts(siblings: [
      new ChainBriefSibling(3, "Write the reader"),
    ])).Value!;

    Assert.Contains("Your work must be COMMITTED before you mark this step done", brief.Content, StringComparison.Ordinal);
    Assert.Contains("`git add -A`", brief.Content, StringComparison.Ordinal);
    Assert.Contains("`git commit -m \"<what you did>\"`", brief.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_ForASequentialStep_RequiresNoCommit()
  {
    ChainBrief brief = ChainBriefRenderer.Render(Facts()).Value!;

    Assert.DoesNotContain("git add -A", brief.Content, StringComparison.Ordinal);
  }

  /// <summary>The reconciler merges the members' branches, so its brief names each one by
  ///     branch and directory (D7/D14). A branch the brief had to guess is a merge that lands
  ///     somewhere the member never committed.</summary>
  [Fact]
  public void Render_ForAReconciler_NamesEveryBranchItMustMerge()
  {
    ChainBrief brief = ChainBriefRenderer.Render(ReconcilerFacts(new ChainBriefReconciliation(
        [
          new ChainBriefBranch(2, "chain-7-step-2", ".worktrees/chain-7-step-2"),
          new ChainBriefBranch(3, "chain-7-step-3", ".worktrees/chain-7-step-3"),
        ],
        [],
        []))).Value!;

    Assert.Contains("## You are the RECONCILER", brief.Content, StringComparison.Ordinal);
    Assert.Contains("- Step 2 — branch `chain-7-step-2` at .worktrees/chain-7-step-2",
        brief.Content, StringComparison.Ordinal);
    Assert.Contains("- Step 3 — branch `chain-7-step-3` at .worktrees/chain-7-step-3",
        brief.Content, StringComparison.Ordinal);
    Assert.Contains("Nothing else follows until you do", brief.Content, StringComparison.Ordinal);
  }

  /// <summary>The checkout's uncommitted files are TOLD, not refused (D14): git is the model's
  ///     to handle, and the brief names what is there so it can decide.</summary>
  [Fact]
  public void Render_ForAReconciler_NamesTheDirtyFilesInTheWorkspaceCheckout()
  {
    ChainBrief brief = ChainBriefRenderer.Render(ReconcilerFacts(new ChainBriefReconciliation(
        [new ChainBriefBranch(2, "chain-7-step-2", ".worktrees/chain-7-step-2")],
        ["src/A.cs"],
        []))).Value!;

    Assert.Contains("## The workspace checkout", brief.Content, StringComparison.Ordinal);
    Assert.Contains("src/A.cs", brief.Content, StringComparison.Ordinal);
    Assert.Contains("They are not yours", brief.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("The workspace checkout is clean.", brief.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_ForAReconciler_WithACleanCheckout_SaysSo()
  {
    ChainBrief brief = ChainBriefRenderer.Render(ReconcilerFacts(new ChainBriefReconciliation(
        [new ChainBriefBranch(2, "chain-7-step-2", ".worktrees/chain-7-step-2")],
        [],
        []))).Value!;

    Assert.Contains("The workspace checkout is clean.", brief.Content, StringComparison.Ordinal);
  }

  /// <summary>A member that failed did not land, and the reconciler is the only session left
  ///     that can say so: its session is closed by the time the merge runs.</summary>
  [Fact]
  public void Render_ForAReconciler_NamesTheStepsThatDidNotSucceed()
  {
    ChainBrief brief = ChainBriefRenderer.Render(ReconcilerFacts(new ChainBriefReconciliation(
        [new ChainBriefBranch(2, "chain-7-step-2", ".worktrees/chain-7-step-2")],
        [],
        [2]))).Value!;

    Assert.Contains("Step(s) 2 did not succeed", brief.Content, StringComparison.Ordinal);
    Assert.Contains("the harness parks the chain and names them", brief.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_ForASequentialStep_IsNotTheReconciler()
  {
    ChainBrief brief = ChainBriefRenderer.Render(Facts()).Value!;

    Assert.DoesNotContain("RECONCILER", brief.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("## The workspace checkout", brief.Content, StringComparison.Ordinal);
  }

  /// <summary>An unreadable checkout is a THIRD answer, distinct from clean: a brief that said
  ///     'clean' when the status query failed would send the reconciler into a merge it was
  ///     never told about.</summary>
  [Fact]
  public void Render_ForAReconciler_WhenTheCheckoutCouldNotBeRead_SaysSoRatherThanClaimingItIsClean()
  {
    ChainBrief brief = ChainBriefRenderer.Render(ReconcilerFacts(new ChainBriefReconciliation(
        [new ChainBriefBranch(2, "chain-7-step-2", ".worktrees/chain-7-step-2")],
        [],
        [],
        CheckoutReadable: false))).Value!;

    Assert.Contains("The workspace checkout could not be read; check it yourself before merging.",
        brief.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("The workspace checkout is clean.", brief.Content, StringComparison.Ordinal);
  }
}
