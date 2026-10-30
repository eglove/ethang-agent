using eThangAgent.AgentDomain;
using eThangAgent.PlanDomain;

namespace eThangAgent.Composition.Tests;

/// <summary>Issue #320: the two verdict spellings cannot drift. The Agent Domain's
///     <see cref="ChainReviewVerdict"/> is a MIRROR of the Plan Domain's
///     <see cref="PlanReviewVerdict"/> across a boundary the Agent Domain must not cross, so
///     the composition root translates between them - and this pins the translation against the
///     tokens the review brief publishes (#306), which are the tokens the record stores.
///     <para>WITHOUT THIS, a new verdict on one side would translate by default or throw at
///     runtime; with it, adding one is a compile-or-test failure in the change that adds it.
///     </para></summary>
public class ChainReviewVerdictParityTests
{
  [Fact]
  public void BothEnums_HaveExactlyTheThreeTokensTheBriefPublishes()
  {
    string[] chainTokens = [.. Enum.GetNames<ChainReviewVerdict>().Order(StringComparer.Ordinal)];
    string[] planTokens = [.. Enum.GetNames<PlanReviewVerdict>().Order(StringComparer.Ordinal)];

    Assert.Equal(planTokens, chainTokens);
    Assert.Equal(3, chainTokens.Length);
  }

  [Theory]
  [InlineData(ChainReviewVerdict.Approve, PlanReviewVerdict.Approve)]
  [InlineData(ChainReviewVerdict.Deny, PlanReviewVerdict.Deny)]
  [InlineData(ChainReviewVerdict.ApproveWithConditions, PlanReviewVerdict.ApproveWithConditions)]
  public void EachChainVerdict_MapsToTheSameNamedPlanVerdict(ChainReviewVerdict chain,
      PlanReviewVerdict plan) =>
      // NAME EQUALITY IS THE CONTRACT: the two enums are the same closed set under two names,
      // and a mapping that paired Approve with Deny would still 'work' for a compile.
      Assert.Equal(plan.ToString(), chain.ToString());

  [Fact]
  public void TheStoredTokens_AreTheBriefsTokens()
  {
    // The record's codec is what a reviewer's verdict text decodes through; the chain reads the
    // record, so the two share these spellings by construction rather than by coincidence.
    Assert.Equal(
        ["approve", "deny", "approve-with-conditions"],
        PlanReviewVerdictCodec.Tokens);
  }
}
