using eThangAgent.AgentDomain;

namespace eThangAgent.Agent.Domain.Tests;

/// <summary>The chain contract's renderer (issue #311 D1/D4). PURE, so every assertion here is
///     about the TEXT: what the contract must name, and what it must not.
///     <para>WHY THE NEGATIVE ASSERTIONS MATTER: the acceptance criterion is that the contract
///     'does not duplicate the chain skill's procedure'. A test that only checked for the
///     presence of the four required facts would pass on a prompt that also restated the whole
///     brief, which is the drift the criterion exists to prevent.</para></summary>
public sealed class ChainContractRendererTests
{
  private static ChainContract Contract(ChainStepRole? role = null) =>
      new(ChainId: 7, PlanId: 104, Position: 2, LedgerKey: "sci.dashboard-truth/ledger",
          Role: role);

  [Fact]
  public void Render_NamesTheAssignedStepAndItsChain()
  {
    string text = ChainContractRenderer.Render(Contract());

    Assert.StartsWith(ChainContractRenderer.Prefix, text, StringComparison.Ordinal);
    Assert.Contains("You are step 2 of chain #7, executing plan #104.", text, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_StatesThatTheHarnessStartsTheNextSession()
  {
    // The fact a session cannot infer from its tool list: nothing else decides whether the
    // chain continues, so a session that tried to start the next step itself would be wrong.
    string text = ChainContractRenderer.Render(Contract());

    Assert.Contains("will start the next session when you mark", text, StringComparison.Ordinal);
    Assert.Contains("not you, and not a parent agent", text, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_NamesTheLedgerKeyAndSaysItIsChainScope()
  {
    string text = ChainContractRenderer.Render(Contract());

    Assert.Contains("state.get key=\"sci.dashboard-truth/ledger\"", text, StringComparison.Ordinal);
    Assert.Contains("Every step of this chain reads and writes the same ledger", text, StringComparison.Ordinal);
  }

  /// <summary>The standing rule the prompt carries: the session chooses its own verification,
  ///     and a run from before the last edit does not count. Naming a command set here would put
  ///     a workspace's tooling in every request and drift from the ledger the gate actually
  ///     reads.</summary>
  [Fact]
  public void Render_StatesTheFreshnessRule_WithoutNamingACommandSet()
  {
    string text = ChainContractRenderer.Render(Contract());

    Assert.Contains("## What backs a done-claim", text, StringComparison.Ordinal);
    Assert.Contains("exits 0", text, StringComparison.Ordinal);
    Assert.Contains("does not count", text, StringComparison.Ordinal);
    Assert.DoesNotContain("goal/check", text, StringComparison.Ordinal);
    Assert.DoesNotContain("state.checkgoal", text, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_NamesTheCompletionRuleWithoutRestatingTheBrief()
  {
    // The brief (#302) carries the plan's goal, the step's title and detail, the predecessors,
    // and the literal update command. The contract must not repeat any of it: the prompt is in
    // every request, so a copy there is paid for on every iteration and free to drift.
    string text = ChainContractRenderer.Render(Contract());

    Assert.Contains("Mark step 2 of plan #104 done", text, StringComparison.Ordinal);
    Assert.DoesNotContain("plan.update-step", text, StringComparison.Ordinal);
    Assert.DoesNotContain("## Your step", text, StringComparison.Ordinal);
    Assert.DoesNotContain("## The plan's goal", text, StringComparison.Ordinal);
    Assert.DoesNotContain("## Steps that already ran", text, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_IsPure_TwoCallsProduceTheSameBytes()
  {
    // The purity contract (issue #267): the prompt is the first bytes of every request, so a
    // renderer that drifted between calls would invalidate the provider's prompt cache.
    ChainContract contract = Contract();

    Assert.Equal(ChainContractRenderer.Render(contract), ChainContractRenderer.Render(contract),
        StringComparer.Ordinal);
  }

  [Fact]
  public void For_DerivesTheContractFromTheBriefFacts()
  {
    // ONE DERIVATION SITE: the brief and the contract come from the same facts, so the ledger
    // key cannot disagree between a session's first message and its prompt.
    ChainBriefFacts facts = new(
        ChainId: 7, ChainTitle: "Dashboard truth", PlanId: 104, PlanVersion: 3,
        PlanGoal: "the status line tells the truth", AssignedPosition: 2,
        PlanSteps: [new ChainBriefPlanStep(2, "rewrite it", null)],
        Predecessors: [],
        LedgerKey: ChainLedgerSlug.KeyFor("Dashboard truth"));

    ChainContract contract = ChainContract.For(facts);

    Assert.Equal(7, contract.ChainId);
    Assert.Equal(104, contract.PlanId);
    Assert.Equal(2, contract.Position);
    Assert.Equal("sci.dashboard-truth/ledger", contract.LedgerKey);
  }

  [Fact]
  public void EffectiveRole_IsImplement_WhenTheContractCarriedNone()
  {
    // An absent role is 'implement', the same default the chain record applies - so a contract
    // written before this issue existed reads as the implementer contract it always was.
    ChainContract contract = new(1, 2, 3, "sci.x/ledger");

    Assert.Equal(ChainStepRoleKind.Implement, contract.EffectiveRole.Kind);
    Assert.False(contract.IsRefine);
  }

  [Fact]
  public void Render_ForARefineStep_MakesThePlanItsWorkRatherThanTheFeature()
  {
    string text = ChainContractRenderer.Render(Contract(role: ChainStepRole.Refine));

    // The standing rule, which is what survives a compaction that evicts the brief.
    Assert.Contains("assigned the refine role", text, StringComparison.Ordinal);
    Assert.Contains("EXECUTABLE, not to implement it", text, StringComparison.Ordinal);
    Assert.DoesNotContain("Mark step 2 of plan #104 done when — and only when — the work", text, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_ForARefineStep_SaysThePlanBacksTheDoneClaim()
  {
    // The gate does not apply to a refine step, and the session must be told so: a refiner
    // looking for a command to run would run one that proves nothing about the plan.
    string text = ChainContractRenderer.Render(Contract(role: ChainStepRole.Refine));

    Assert.Contains("backed by the PLAN, not by a command", text, StringComparison.Ordinal);
    Assert.Contains("completion gate does not apply to this step", text, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_ForARefineStep_StillSaysTheHarnessAdvancesTheChain()
  {
    string text = ChainContractRenderer.Render(Contract(role: ChainStepRole.Refine));

    // A refine step is still a chain step: it finishes by marking the step done, and the
    // harness - not the refiner - decides what follows.
    Assert.Contains("will start the next session when you mark", text, StringComparison.Ordinal);
    Assert.Contains("step of this plan you may mark done", text, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_ForAnImplementStep_IsUnchangedByTheRoleBranch()
  {
    string byDefault = ChainContractRenderer.Render(Contract());
    string explicitImplement = ChainContractRenderer.Render(Contract(role: ChainStepRole.Implement));

    Assert.Equal(byDefault, explicitImplement, StringComparer.Ordinal);
    Assert.DoesNotContain("review role", byDefault, StringComparison.Ordinal);
    Assert.Contains("Mark step 2 of plan #104 done when", byDefault, StringComparison.Ordinal);
  }
}
