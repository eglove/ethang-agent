using eThangAgent.AgentDomain;

namespace eThangAgent.Agent.Domain.Tests;

/// <summary>RED: the final agent's brief (chain completed). These pin the TEXT: the final
///     agent is seeded with a summary of the work done and a retrospective instruction,
///     and it must NOT carry a completion contract (there is no step left to mark).</summary>
public sealed class ChainFinalBriefRendererTests
{
  private static ChainBriefFacts Facts(
      IReadOnlyList<ChainBriefPredecessor>? predecessors = null) =>
      new(ChainId: 7,
          ChainTitle: "2026-08-24-dashboard-truth",
          PlanId: 104,
          PlanVersion: 3,
          PlanGoal: "Make the dashboard tell the truth about what it did.",
          AssignedPosition: 0,
          PlanSteps: [
            new ChainBriefPlanStep(1, "Honest status line", "Rewrite the placed/refused summary."),
            new ChainBriefPlanStep(2, "Real demo broker", null),
          ],
          Predecessors: predecessors ?? [
            new ChainBriefPredecessor(1, ChainStepStatus.Done, "status line rewritten"),
            new ChainBriefPredecessor(2, ChainStepStatus.Done, "broker wired"),
          ],
          LedgerKey: "sci.dashboard-truth/ledger",
          Role: null,
          ReviewRubricAvailable: false,
          IsFinal: true);

  [Fact]
  public void Render_ForFinal_NamesTheChainCompleteAndListsEveryStep()
  {
    ChainBrief brief = ChainBriefRenderer.Render(Facts()).Value!;

    Assert.StartsWith(ChainFinalBriefRenderer.Prefix, brief.Content, StringComparison.Ordinal);
    Assert.Contains("chain #7 is complete", brief.Content, StringComparison.Ordinal);
    Assert.Contains("Step 1", brief.Content, StringComparison.Ordinal);
    Assert.Contains("Step 2", brief.Content, StringComparison.Ordinal);
    Assert.Contains("status line rewritten", brief.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_ForFinal_InstructsARetrospectiveAndHandBack()
  {
    ChainBrief brief = ChainBriefRenderer.Render(Facts()).Value!;

    Assert.Contains("## Retrospective", brief.Content, StringComparison.Ordinal);
    Assert.Contains("hand control back to the user", brief.Content, StringComparison.Ordinal);
    Assert.Contains("summary of the work done", brief.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_ForFinal_CarriesNoCompletionContract()
  {
    ChainBrief brief = ChainBriefRenderer.Render(Facts()).Value!;

    Assert.DoesNotContain("plan.update-step id=104", brief.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("ONLY step you may mark done", brief.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("You are step", brief.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_ForFinal_RefusesNothing() =>
    // A final brief has no assigned step, so the step-missing refusal must not fire.
    Assert.True(ChainBriefRenderer.Render(Facts()).IsSuccess);
}
