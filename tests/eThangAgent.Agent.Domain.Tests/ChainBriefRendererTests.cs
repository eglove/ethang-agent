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
      IReadOnlyList<string>? gate = null,
      string goal = "Make the dashboard tell the truth about what it did.",
      ChainStepRole? role = null,
      bool rubric = false) =>
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
          GateCommands: gate ?? [],
          LedgerKey: "sci.dashboard-truth/ledger",
          Role: role,
          ReviewRubricAvailable: rubric);

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

  [Fact]
  public void Render_WithGateCommands_ListsThemAndDemandsFreshness()
  {
    ChainBrief brief = ChainBriefRenderer.Render(
        Facts(gate: ["dotnet build", "dotnet test"])).Value!;

    Assert.Contains("    dotnet build", brief.Content, StringComparison.Ordinal);
    Assert.Contains("    dotnet test", brief.Content, StringComparison.Ordinal);
    Assert.Contains("state.checkgoal", brief.Content, StringComparison.Ordinal);
    Assert.Contains("FRESH", brief.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_WithNoGate_SaysSoRatherThanStayingSilent()
  {
    ChainBrief brief = ChainBriefRenderer.Render(Facts()).Value!;

    Assert.Contains("declares no goal/check commands", brief.Content, StringComparison.Ordinal);
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
}
