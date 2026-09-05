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
    p = p.AddStep("first", null, null).AddStep("second", "detail", 3);
    Assert.Equal(2, p.Steps.Count);
    Assert.Equal(1, p.Steps[0].Position);
    Assert.Equal(2, p.Steps[1].Position);
    Assert.Equal(3, p.Steps[1].TodoId);
    Assert.True(p.UpdatedAt >= t0);
  }

  [Fact]
  public void AddStep_OnCompletedPlan_Throws()
  {
    Plan p = NewPlan().SetStatus(PlanStatus.Completed, DateTimeOffset.UtcNow);
    _ = Assert.Throws<PlanInputException>(() => p.AddStep("x", null, null));
  }

  [Fact]
  public void RemoveStep_RenumbersTail()
  {
    Plan p = NewPlan().AddStep("a", null, null).AddStep("b", null, null).AddStep("c", null, null);
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
    Plan p = NewPlan().AddStep("a", null, null);
    p = p.UpdateStep(1, s => s with { Status = PlanStepStatus.Done, TodoId = 7 });
    Assert.Equal(PlanStepStatus.Done, p.Steps[0].Status);
    Assert.Equal(7, p.Steps[0].TodoId);
  }

  [Fact]
  public void SetStatus_ActiveToCompleted_Succeeds_AndFreezes()
  {
    Plan p = NewPlan().SetStatus(PlanStatus.Completed, DateTimeOffset.UtcNow);
    Assert.Equal(PlanStatus.Completed, p.Status);
    _ = Assert.Throws<PlanInputException>(() => p.AddStep("x", null, null));
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
}
