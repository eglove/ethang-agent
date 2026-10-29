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
  private static ChainContract Contract(IReadOnlyList<string>? gate = null) =>
      new(ChainId: 7, PlanId: 104, Position: 2, LedgerKey: "sci.dashboard-truth/ledger",
          GateCommands: gate);

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

  [Fact]
  public void Render_WithAGate_ListsItAndDemandsFreshness()
  {
    string text = ChainContractRenderer.Render(Contract(["dotnet build", "dotnet test"]));

    Assert.Contains("    dotnet build", text, StringComparison.Ordinal);
    Assert.Contains("    dotnet test", text, StringComparison.Ordinal);
    Assert.Contains("FRESH passing gate", text, StringComparison.Ordinal);
    Assert.Contains("does not count", text, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_WithNoGate_SaysNoGateApplies_RatherThanSayingNothing()
  {
    // An unset goal/check never blocks (the CompletionGate rule), and 'no gate applies' is a
    // different fact from silence: a session that read silence would wonder whether it had
    // missed one.
    string text = ChainContractRenderer.Render(Contract());

    Assert.Contains("declares no goal/check commands", text, StringComparison.Ordinal);
    Assert.DoesNotContain("state.checkgoal runs them", text, StringComparison.Ordinal);
  }

  [Fact]
  public void Render_NamesTheCompletionRuleWithoutRestatingTheBrief()
  {
    // The brief (#302) carries the plan's goal, the step's title and detail, the predecessors,
    // and the literal update command. The contract must not repeat any of it: the prompt is in
    // every request, so a copy there is paid for on every iteration and free to drift.
    string text = ChainContractRenderer.Render(Contract(["dotnet build"]));

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
    ChainContract contract = Contract(["dotnet build"]);

    Assert.Equal(ChainContractRenderer.Render(contract), ChainContractRenderer.Render(contract),
        StringComparer.Ordinal);
  }

  [Fact]
  public void For_DerivesTheContractFromTheBriefFacts()
  {
    // ONE DERIVATION SITE: the brief and the contract come from the same facts, so the ledger
    // key and the gate cannot disagree between a session's first message and its prompt.
    ChainBriefFacts facts = new(
        ChainId: 7, ChainTitle: "Dashboard truth", PlanId: 104, PlanVersion: 3,
        PlanGoal: "the status line tells the truth", AssignedPosition: 2,
        PlanSteps: [new ChainBriefPlanStep(2, "rewrite it", null)],
        Predecessors: [], GateCommands: ["dotnet build"],
        LedgerKey: ChainLedgerSlug.KeyFor("Dashboard truth"));

    ChainContract contract = ChainContract.For(facts);

    Assert.Equal(7, contract.ChainId);
    Assert.Equal(104, contract.PlanId);
    Assert.Equal(2, contract.Position);
    Assert.Equal("sci.dashboard-truth/ledger", contract.LedgerKey);
    Assert.Equal(["dotnet build"], contract.Gates);
  }

  [Fact]
  public void Gates_IsNeverNull_EvenWhenThePayloadCarriedNone() =>
      Assert.Empty(new ChainContract(1, 2, 3, "sci.x/ledger").Gates);
}
