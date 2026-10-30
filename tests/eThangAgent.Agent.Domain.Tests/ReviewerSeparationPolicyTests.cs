using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Issue #218: the reviewer-separation rule. PURE, so these are domain tests with no
///     store, no clock, and no session - the comparison is over the CHAIN RECORD (D3), which is
///     what makes it survive a restart and work after the implementer's session has closed.
///     </summary>
public class ReviewerSeparationPolicyTests
{
  private static readonly DateTimeOffset T0 = new(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);

  /// <summary>A chain whose steps are the given roles, in position order, with the
  ///     implementers' recorded served models supplied.</summary>
  private static Chain ChainOf(params (ChainStepRole Role, string? ServedModel)[] steps)
  {
    Chain chain = Chain.NewFromSeeds("C:\\ws", 3, 1, "t", "session-root",
        [.. steps.Select((step, index) => new ChainStepSeed(index + 1, step.Role))], T0);
    for (int i = 0; i < steps.Length; i++)
    {
      if (steps[i].ServedModel is { } model)
      {
        chain = chain.RecordServedModel(i + 1, model, T0);
      }
    }

    return chain;
  }

  [Fact]
  public void MissingModel_AnImplementerStepNeedsNone() =>
      Assert.Null(ReviewerSeparationPolicy.MissingModel(ChainStepRole.Implement, position: 1));

  [Fact]
  public void MissingModel_AReviewStepWithNoModel_IsRefusedByName()
  {
    DomainError? error = ReviewerSeparationPolicy.MissingModel(ChainStepRole.Review(), position: 2);

    Assert.NotNull(error);
    Assert.Equal(ReviewerSeparationCode.ModelMissing, error.Code);
    Assert.Contains("step 2", error.Message, StringComparison.Ordinal);
    Assert.Contains("review role", error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void MissingModel_AReviewStepWithAModel_IsAccepted() =>
      Assert.Null(ReviewerSeparationPolicy.MissingModel(ChainStepRole.Review("vendor/reviewer"), position: 2));

  [Fact]
  public void NotSeparated_AReviewerOnTheImplementersModel_IsRefusedNamingBothModelsAndTheStep()
  {
    Chain chain = ChainOf(
        (ChainStepRole.Implement, "vendor/implementer"),
        (ChainStepRole.Review("vendor/implementer"), null));

    DomainError? error = ReviewerSeparationPolicy.NotSeparated(chain, chain.StepAt(2)!);

    Assert.NotNull(error);
    Assert.Equal(ReviewerSeparationCode.ModelMatchesImplementer, error.Code);
    Assert.Contains("step 2", error.Message, StringComparison.Ordinal);
    Assert.Contains("step 1", error.Message, StringComparison.Ordinal);
    Assert.Contains("vendor/implementer", error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void NotSeparated_ADifferentModel_IsAccepted()
  {
    Chain chain = ChainOf(
        (ChainStepRole.Implement, "vendor/implementer"),
        (ChainStepRole.Review("vendor/reviewer"), null));

    Assert.Null(ReviewerSeparationPolicy.NotSeparated(chain, chain.StepAt(2)!));
  }

  [Fact]
  public void NotSeparated_B7_TwoSpellingsOfOneModel_AreCaught()
  {
    // D2: the comparison is on the RESOLVED model id. 'vendor/model:nitro' and 'vendor/model'
    // are one model served two ways, so comparing the request strings would pass a review that
    // runs on the implementer's model.
    Chain chain = ChainOf(
        (ChainStepRole.Implement, "vendor/model"),
        (ChainStepRole.Review("vendor/model:nitro"), null));

    DomainError? error = ReviewerSeparationPolicy.NotSeparated(chain, chain.StepAt(2)!);

    Assert.NotNull(error);
    Assert.Equal(ReviewerSeparationCode.ModelMatchesImplementer, error.Code);
  }

  [Fact]
  public void NotSeparated_ACaseDifference_IsStillTheSameModel()
  {
    Chain chain = ChainOf(
        (ChainStepRole.Implement, "Vendor/Model"),
        (ChainStepRole.Review("vendor/model"), null));

    Assert.NotNull(ReviewerSeparationPolicy.NotSeparated(chain, chain.StepAt(2)!));
  }

  [Fact]
  public void NotSeparated_AnUnrecordedImplementerModel_IsNotAMatch()
  {
    // NULL means 'not recorded', never 'no model'. Fabricating a comparison from a model nobody
    // recorded would refuse work on a guess.
    Chain chain = ChainOf(
        (ChainStepRole.Implement, null),
        (ChainStepRole.Review("vendor/reviewer"), null));

    Assert.Null(ReviewerSeparationPolicy.NotSeparated(chain, chain.StepAt(2)!));
  }

  [Fact]
  public void NotSeparated_AReviewStepWithNoPrecedingImplementer_IsAccepted()
  {
    // A chain of reviews has no implementer, and a reviewer cannot be biased toward work it
    // did not do.
    Chain chain = ChainOf((ChainStepRole.Review("vendor/reviewer"), null));

    Assert.Null(ReviewerSeparationPolicy.NotSeparated(chain, chain.StepAt(1)!));
  }

  [Fact]
  public void ImplementerFor_ReadsTheNearestPrecedingImplementer()
  {
    Chain chain = ChainOf(
        (ChainStepRole.Implement, "vendor/first"),
        (ChainStepRole.Implement, "vendor/second"),
        (ChainStepRole.Review("vendor/reviewer"), null));

    ChainStep? implementer = ReviewerSeparationPolicy.ImplementerFor(chain, reviewPosition: 3);

    Assert.NotNull(implementer);
    Assert.Equal(2, implementer.Position);
    Assert.Equal("vendor/second", implementer.ServedModel);
  }

  [Fact]
  public void RefusalFor_ReportsTheMissingModelBeforeTheMatch()
  {
    // Order is the order they matter in: a step that names no reviewer model cannot be compared
    // at all.
    Chain chain = ChainOf(
        (ChainStepRole.Implement, "vendor/implementer"),
        (ChainStepRole.Review(), null));

    DomainError? error = ReviewerSeparationPolicy.RefusalFor(chain, position: 2);

    Assert.NotNull(error);
    Assert.Equal(ReviewerSeparationCode.ModelMissing, error.Code);
  }

  [Fact]
  public void RefusalFor_AnImplementerStep_IsAlwaysCleared()
  {
    Chain chain = ChainOf((ChainStepRole.Implement, "vendor/implementer"));

    Assert.Null(ReviewerSeparationPolicy.RefusalFor(chain, position: 1));
  }

  [Fact]
  public void RefusalFor_B3_SurvivesARestart_WhenTheChainIsReloadedFromItsRecord()
  {
    // The implementer's model is a RECORDED fact, not a live parent's configuration: a chain
    // rebuilt from its own record refuses identically, which is what makes the rule work after a
    // restart and when the implementer's session has closed.
    Chain reloaded = ChainOf(
        (ChainStepRole.Implement, "vendor/implementer"),
        (ChainStepRole.Review("vendor/implementer"), null));

    DomainError? error = ReviewerSeparationPolicy.RefusalFor(reloaded, position: 2);

    Assert.NotNull(error);
    Assert.Equal(ReviewerSeparationCode.ModelMatchesImplementer, error.Code);
  }
}
