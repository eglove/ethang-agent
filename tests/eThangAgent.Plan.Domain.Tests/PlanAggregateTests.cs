using Xunit;

namespace eThangAgent.PlanDomain.Tests;

public class PlanAggregateTests
{
  private static Plan NewPlan() => Plan.New("Ship it", "Goal body", "0d3c8a2e-1c4b-4f5a-9a2b-3c4d5e6f7a8b", DateTimeOffset.UtcNow);

  [Fact]
  public void New_IsActive_VersionZero_IdUnassigned()
  {
    Plan p = NewPlan();
    Assert.Equal(PlanStatus.Active, p.Status);
    Assert.Equal(0, p.Version);
    Assert.Equal(0, p.Id);
    Assert.Empty(p.Steps);
  }

  [Theory]
  [InlineData("")]
  [InlineData("   ")]
  public void New_RejectsEmptyTitle(string title) =>
    Assert.Throws<ArgumentException>(() => Plan.New(title, "goal", "s", DateTimeOffset.UtcNow));

  [Fact]
  public void AddStep_AssignsContiguousPositions_AndStampsTimestamp()
  {
    DateTimeOffset t0 = DateTimeOffset.UtcNow;
    Plan p = NewPlan();
    p = p.AddStep("first", null).AddStep("second", "detail");
    Assert.Equal(2, p.Steps.Count);
    Assert.Equal(1, p.Steps[0].Position);
    Assert.Equal(2, p.Steps[1].Position);
    Assert.Equal("detail", p.Steps[1].Detail);
    Assert.True(p.UpdatedAt >= t0);
  }

  [Fact]
  public void AddStep_OnCompletedPlan_Throws()
  {
    Plan p = NewPlan().SetStatus(PlanStatus.Completed, DateTimeOffset.UtcNow);
    _ = Assert.Throws<PlanInputException>(() => p.AddStep("x", null));
  }

  [Fact]
  public void RemoveStep_RenumbersTail()
  {
    Plan p = NewPlan().AddStep("a", null).AddStep("b", null).AddStep("c", null);
    p = p.RemoveStep(1);
    Assert.Equal(["b", "c"], [.. p.Steps.Select(s => s.Title)]);
    Assert.Equal([1, 2], [.. p.Steps.Select(s => s.Position)]);
  }

  [Fact]
  public void RemoveStep_UnknownPosition_Throws() =>
    Assert.Throws<PlanInputException>(() => NewPlan().RemoveStep(9));

  [Fact]
  public void UpdateStep_AppliesMutation()
  {
    Plan p = NewPlan().AddStep("a", null);
    p = p.UpdateStep(1, s => s with { Status = PlanStepStatus.Done });
    Assert.Equal(PlanStepStatus.Done, p.Steps[0].Status);
    Assert.Equal(PlanStepStatus.Done, p.Steps[0].Status);
  }

  [Fact]
  public void SetStatus_ActiveToCompleted_Succeeds_AndFreezes()
  {
    Plan p = NewPlan().SetStatus(PlanStatus.Completed, DateTimeOffset.UtcNow);
    Assert.Equal(PlanStatus.Completed, p.Status);
    _ = Assert.Throws<PlanInputException>(() => p.AddStep("x", null));
    _ = Assert.Throws<PlanInputException>(() => p.SetStatus(PlanStatus.Active, DateTimeOffset.UtcNow));
  }

  [Fact]
  public void TransitionSpecification_RejectsTerminalToActive()
  {
    Plan done = NewPlan().SetStatus(PlanStatus.Completed, DateTimeOffset.UtcNow);
    Assert.False(new PlanStatusTransition(PlanStatus.Active).IsSatisfiedBy(done));
  }

  [Fact]
  public void TransitionSpecification_AllowsActiveToCompleted() =>
    Assert.True(new PlanStatusTransition(PlanStatus.Completed).IsSatisfiedBy(NewPlan()));

  // ---- completion invariant (issue #271) ------------------------------------

  [Fact]
  public void SetStatus_Completed_WithPendingStep_Throws_NamingPositionAndStatus()
  {
    Plan p = NewPlan().AddStep("a", null).AddStep("b", null)
      .UpdateStep(1, s => s with { Status = PlanStepStatus.Done });
    PlanInputException ex = Assert.Throws<PlanInputException>(
      () => p.SetStatus(PlanStatus.Completed, DateTimeOffset.UtcNow));
    Assert.Contains("2", ex.Message, StringComparison.Ordinal);
    Assert.Contains("Pending", ex.Message, StringComparison.Ordinal);
    Assert.DoesNotContain("1.", ex.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void SetStatus_Completed_WithInProgressStep_Throws()
  {
    Plan p = NewPlan().AddStep("a", null)
      .UpdateStep(1, s => s with { Status = PlanStepStatus.InProgress });
    PlanInputException ex = Assert.Throws<PlanInputException>(
      () => p.SetStatus(PlanStatus.Completed, DateTimeOffset.UtcNow));
    Assert.Contains("InProgress", ex.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void SetStatus_Completed_WhenEveryStepDoneOrSkipped_Succeeds()
  {
    Plan p = NewPlan().AddStep("a", null).AddStep("b", null)
      .UpdateStep(1, s => s with { Status = PlanStepStatus.Done })
      .UpdateStep(2, s => s with { Status = PlanStepStatus.Skipped });
    Plan completed = p.SetStatus(PlanStatus.Completed, DateTimeOffset.UtcNow);
    Assert.Equal(PlanStatus.Completed, completed.Status);
  }

  [Fact]
  public void SetStatus_Abandoned_WithPendingSteps_StillSucceeds()
  {
    Plan p = NewPlan().AddStep("a", null);
    Plan abandoned = p.SetStatus(PlanStatus.Abandoned, DateTimeOffset.UtcNow);
    Assert.Equal(PlanStatus.Abandoned, abandoned.Status);
  }

  [Fact]
  public void TransitionSpecification_RejectsCompletion_WhileAStepIsUnfinished()
  {
    Plan p = NewPlan().AddStep("a", null);
    Assert.False(new PlanStatusTransition(PlanStatus.Completed).IsSatisfiedBy(p));
    Assert.True(new PlanStatusTransition(PlanStatus.Abandoned).IsSatisfiedBy(p));
  }

  [Fact]
  public void Skipped_IsADefinedStepStatus()
  {
    Assert.True(Enum.IsDefined(PlanStepStatus.Skipped));
    Assert.Equal("Skipped", PlanStepStatus.Skipped.ToString());
  }
}
