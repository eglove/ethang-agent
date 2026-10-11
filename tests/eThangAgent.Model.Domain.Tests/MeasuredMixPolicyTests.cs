using eThangAgent.ModelDomain;

namespace eThangAgent.Model.Domain.Tests;

public class MeasuredMixPolicyTests
{
  [Fact]
  public void Observe_SeedsFromPrior_ThenEwmas()
  {
    MeasuredMixPolicy policy = new(seedWeight: 1.0m);

    policy.Observe(new UsageObservation(100, 300));

    Assert.Equal(1.6m, policy.MeasuredWeight);
  }

  [Fact]
  public void Observe_ZeroInputTurn_Skipped()
  {
    MeasuredMixPolicy policy = new(null);

    policy.Observe(new UsageObservation(0, 500));

    Assert.Equal(0, policy.Totals.Turns);
    Assert.Equal(0L, policy.Totals.TotalInput);
    Assert.Equal(0L, policy.Totals.TotalOutput);
    Assert.Equal(MeasuredMixPolicy.DefaultWeight, policy.MeasuredWeight);
  }

  [Fact]
  public void Observe_AccumulatesTotals()
  {
    MeasuredMixPolicy policy = new(null);

    policy.Observe(new UsageObservation(100, 10));
    policy.Observe(new UsageObservation(200, 20));

    Assert.Equal(300L, policy.Totals.TotalInput);
    Assert.Equal(30L, policy.Totals.TotalOutput);
    Assert.Equal(2, policy.Totals.Turns);
  }

  [Fact]
  public void TrustGate_FiveTurns_Enables()
  {
    MeasuredMixPolicy policy = new(null);

    for (int i = 0; i < 4; i++)
    {
      policy.Observe(new UsageObservation(100, 10));
      Assert.False(policy.IsTrusted);
    }

    policy.Observe(new UsageObservation(100, 10));

    Assert.True(policy.IsTrusted);
  }

  [Fact]
  public void ResetTrust_RestartsCount_KeepsWeight()
  {
    MeasuredMixPolicy policy = new(null);
    for (int i = 0; i < 6; i++)
    {
      policy.Observe(new UsageObservation(100, 10));
    }

    decimal weight = policy.MeasuredWeight;
    (long input, long output, int turns) = (policy.Totals.TotalInput, policy.Totals.TotalOutput, policy.Totals.Turns);

    policy.ResetTrust();

    Assert.False(policy.IsTrusted);
    Assert.Equal(weight, policy.MeasuredWeight);
    Assert.Equal(input, policy.Totals.TotalInput);
    Assert.Equal(output, policy.Totals.TotalOutput);
    Assert.Equal(turns, policy.Totals.Turns);
  }

  [Fact]
  public void Seed_DefaultsTo3()
  {
    MeasuredMixPolicy policy = new(null);

    Assert.Equal(3m, policy.MeasuredWeight);
    Assert.Equal(3m, MeasuredMixPolicy.DefaultWeight);
  }

  [Fact]
  public void Constants_ArePinned()
  {
    Assert.Equal(5, MeasuredMixPolicy.TrustGateTurns);
    Assert.Equal(0.3m, MeasuredMixPolicy.Alpha);
    Assert.Equal(0.10m, MeasuredMixPolicy.MinMargin);
  }

  private static ModelProviderEntry P(string prov, decimal inM, decimal outM) =>
      new("m/x", prov, inM / 1_000_000m, outM / 1_000_000m, 128000, 0, true, false, 50, 80, null, null, null, null);

  [Fact]
  public void Evaluate_Untrusted_NeverFires()
  {
    MeasuredMixPolicy policy = new(seedWeight: 0.2m);
    policy.Observe(new UsageObservation(100, 5));

    ReselectionConsidered? result = policy.Evaluate([P("A", 0.04m, 0.50m), P("B", 0.07m, 0.05m)]);

    Assert.Null(result);
  }

  [Fact]
  public void Evaluate_TrustedCrossing_WithMargin_Fires_AndRebaselines()
  {
    MeasuredMixPolicy policy = new(3.0m);
    for (int i = 0; i < 13; i++)
    {
      policy.Observe(new UsageObservation(100, 1));
    }

    ModelProviderEntry[] entries = [P("A", 0.04m, 0.50m), P("B", 0.07m, 0.05m)];

    ReselectionConsidered? first = policy.Evaluate(entries);

    Assert.NotNull(first);
    Assert.True(policy.MeasuredWeight < 1m);
    Assert.True(first.NewBlended < first.IncumbentBlended);

    ReselectionConsidered? second = policy.Evaluate(entries);

    Assert.Null(second);
  }

  [Fact]
  public void Evaluate_SubMarginCrossing_Silent()
  {
    MeasuredMixPolicy policy = new(1.0m);
    for (int i = 0; i < 5; i++)
    {
      policy.Observe(new UsageObservation(100, 99));
    }

    ReselectionConsidered? result = policy.Evaluate([P("A", 0.10m, 0.10m), P("B", 0.099m, 0.101m)]);

    Assert.Null(result);
  }

  [Fact]
  public void Evaluate_EmptyEntries_Null()
  {
    MeasuredMixPolicy policy = new(3.0m);
    for (int i = 0; i < 5; i++)
    {
      policy.Observe(new UsageObservation(100, 10));
    }

    Assert.Null(policy.Evaluate([]));
  }

  [Fact]
  public void Evaluate_NullEntries_Null()
  {
    MeasuredMixPolicy policy = new(3.0m);
    for (int i = 0; i < 5; i++)
    {
      policy.Observe(new UsageObservation(100, 10));
    }

    Assert.Null(policy.Evaluate(null!));
  }

  [Fact]
  public void Evaluate_CrossingBack_FiresAgain()
  {
    MeasuredMixPolicy policy = new(3.0m);
    for (int i = 0; i < 13; i++)
    {
      policy.Observe(new UsageObservation(100, 1));
    }

    ModelProviderEntry[] entries = [P("A", 0.04m, 0.50m), P("B", 0.07m, 0.05m)];
    ReselectionConsidered? first = policy.Evaluate(entries);
    Assert.NotNull(first);

    for (int i = 0; i < 5; i++)
    {
      policy.Observe(new UsageObservation(100, 400));
    }

    ReselectionConsidered? second = policy.Evaluate(entries);

    Assert.NotNull(second);
    Assert.True(second.NewBlended < second.IncumbentBlended);
  }
}
