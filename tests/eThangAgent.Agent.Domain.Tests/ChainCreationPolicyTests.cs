using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Issue #313 D4: the creation validates before anything starts. PURE rules, so
///     these are domain tests with no store and no clock.</summary>
public class ChainCreationPolicyTests
{
  private static readonly int[] PlanSteps = [1, 2, 3];

  [Fact]
  public void RefusalFor_AValidDeclaration_IsNull()
  {
    Assert.Null(ChainCreationPolicy.RefusalFor(
        [ChainStepSeed.Implementing(1), ChainStepSeed.Implementing(3)], PlanSteps));
  }

  [Fact]
  public void RefusalFor_NoSteps_IsRefusedAsNoExecutableSteps()
  {
    DomainError? error = ChainCreationPolicy.RefusalFor([], PlanSteps);

    Assert.NotNull(error);
    Assert.Equal(ChainCreationCode.NoExecutableSteps, error.Code);
    Assert.Contains("at least one executable step", error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void RefusalFor_APositionThePlanDoesNotHave_NamesTheMissingAndTheAvailable()
  {
    DomainError? error = ChainCreationPolicy.RefusalFor(
        [ChainStepSeed.Implementing(1), ChainStepSeed.Implementing(9)], PlanSteps);

    Assert.NotNull(error);
    Assert.Equal(ChainCreationCode.StepMissing, error.Code);
    Assert.Contains("position(s) 9", error.Message, StringComparison.Ordinal);
    Assert.Contains("position(s) 1, 2, 3", error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void RefusalFor_AgainstAPlanWithNoSteps_ReportsTheMissingPosition()
  {
    DomainError? error = ChainCreationPolicy.RefusalFor([ChainStepSeed.Implementing(1)], []);

    Assert.NotNull(error);
    Assert.Equal(ChainCreationCode.StepMissing, error.Code);
    Assert.Contains("it has 0 step(s).", error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void RefusalFor_OnePositionWithTwoRoles_IsRefusedNamingBoth()
  {
    DomainError? error = ChainCreationPolicy.RefusalFor(
        [ChainStepSeed.Implementing(1), new ChainStepSeed(1, ChainStepRole.Review("other/model"))],
        PlanSteps);

    Assert.NotNull(error);
    Assert.Equal(ChainCreationCode.ConflictingRole, error.Code);
    Assert.Contains("position 1", error.Message, StringComparison.Ordinal);
    Assert.Contains("Implement", error.Message, StringComparison.Ordinal);
    Assert.Contains("Review", error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void RefusalFor_ADuplicateThatAgrees_IsNotAConflict()
  {
    Assert.Null(ChainCreationPolicy.RefusalFor(
        [ChainStepSeed.Implementing(1), ChainStepSeed.Implementing(1)], PlanSteps));
  }

  [Fact]
  public void RefusalFor_ChecksTheEmptyDeclarationBeforeThePositions()
  {
    // An empty declaration against a plan with no steps earns the D10 refusal, not a
    // missing-position one: 'there is nothing to execute' is the fact that changes what the
    // caller does next.
    DomainError? error = ChainCreationPolicy.RefusalFor([], []);

    Assert.NotNull(error);
    Assert.Equal(ChainCreationCode.NoExecutableSteps, error.Code);
  }
}
