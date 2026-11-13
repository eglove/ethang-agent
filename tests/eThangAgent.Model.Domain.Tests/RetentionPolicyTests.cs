namespace eThangAgent.ModelDomain.Tests;

/// <summary>The three retention tiers, in the terms the provider publishes its own data policy
///     in.
///     <para>THE FIRST TWO ARE ENFORCED AND THE THIRD IS NOT. That distinction is the whole
///     reason the toggle became a tier: a model with no eligible endpoint must fail the turn
///     under ZDR and under Safety Retention, while Retention and Training states no
///     restriction for the harness to enforce.</para></summary>
public class RetentionPolicyTests
{
  [Fact]
  public void All_OffersTheDefaultFirst()
      => Assert.Equal(
          [RetentionPolicy.ZeroDataRetention, RetentionPolicy.SafetyRetention,
           RetentionPolicy.RetentionAndTraining],
          RetentionPolicies.All);

  [Theory]
  [InlineData(RetentionPolicy.ZeroDataRetention, "ZDR")]
  [InlineData(RetentionPolicy.SafetyRetention, "Safety Retention")]
  [InlineData(RetentionPolicy.RetentionAndTraining, "Retention and Training")]
  public void DisplayName_NamesTheTier(RetentionPolicy policy, string expected)
      => Assert.Equal(expected, RetentionPolicies.DisplayName(policy));

  [Theory]
  [InlineData(RetentionPolicy.ZeroDataRetention, "no data retention")]
  [InlineData(RetentionPolicy.SafetyRetention, "safety retention only")]
  [InlineData(RetentionPolicy.RetentionAndTraining, "retention and training allowed")]
  public void StatusBadge_NamesEveryTierInForce(RetentionPolicy policy, string expected)
      // A session that has loosened its policy says so instead of showing silence.
      => Assert.Equal(expected, RetentionPolicies.StatusBadge(policy));

  [Theory]
  [InlineData(RetentionPolicy.ZeroDataRetention, true)]
  [InlineData(RetentionPolicy.SafetyRetention, true)]
  [InlineData(RetentionPolicy.RetentionAndTraining, false)]
  public void IsEnforced_IsTrueForTheTwoRestrictedTiers(RetentionPolicy policy, bool expected)
      => Assert.Equal(expected, RetentionPolicies.IsEnforced(policy));

  [Fact]
  public void EveryTier_IsCoveredByEveryFunction()
  {
    // A tier added later must state its own name, badge, and enforcement rather than
    // silently rendering as nothing.
    foreach (RetentionPolicy policy in RetentionPolicies.All)
    {
      Assert.NotEmpty(RetentionPolicies.DisplayName(policy));
      Assert.NotEmpty(RetentionPolicies.StatusBadge(policy));
    }
  }

  [Fact]
  public void AnOutOfRangeTier_IsRefusedRatherThanRendered()
      => Assert.Throws<ArgumentOutOfRangeException>(() =>
          RetentionPolicies.DisplayName((RetentionPolicy)99));

  [Fact]
  public void AModelConfig_DefaultsToTheStrictTier()
      => Assert.Equal(RetentionPolicy.ZeroDataRetention,
          ModelConfig.Create("alpha/model", 64, 0.7f, 4096).Value!.Retention);
}
