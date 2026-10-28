using eThangAgent.SharedKernel;
using Xunit;

namespace eThangAgent.PlanDomain.Tests;

public class PlanServiceTests
{
  private sealed class FakeStore : IPlanStore
  {
    private readonly Dictionary<int, Plan> _plans = [];
    private int _next = 1;
    public Task<Result<Plan>> CreateAsync(Plan draft, CancellationToken ct = default)
    {
      Plan saved = draft with { Id = _next++, Version = 1 };
      _plans[saved.Id] = saved;
      return Task.FromResult(Result.Success(saved));
    }
    public Task<Result<Plan>> GetAsync(int id, CancellationToken ct = default) =>
      Task.FromResult(_plans.TryGetValue(id, out Plan? p)
        ? Result.Success(p)
        : Result.Failure<Plan>(new DomainError("PlanNotFound", $"no plan #{id}")));
    public Task<Result<PlanCatalog>> ListAsync(PlanStatus? status, CancellationToken ct = default) =>
      Task.FromResult(Result.Success(new PlanCatalog(
        [.. _plans.Values.Where(p => status is null || p.Status == status)], [])));
    public Task<Result<Plan>> SaveAsync(Plan plan, int expectedVersion, CancellationToken ct = default)
    {
      if (!_plans.TryGetValue(plan.Id, out Plan? current))
      {
        return Task.FromResult(Result.Failure<Plan>(new DomainError("PlanNotFound", $"no plan #{plan.Id}")));
      }

      if (current.Version != expectedVersion)
      {
        return Task.FromResult(Result.Failure<Plan>(new DomainError("VersionConflict",
          $"plan #{plan.Id} is at v{current.Version}; expectedVersion was {expectedVersion}")));
      }

      Plan saved = plan with { Version = current.Version + 1 };
      _plans[saved.Id] = saved;
      return Task.FromResult(Result.Success(saved));
    }
  }

  private static (PlanService Service, FakeStore Store) New()
  {
    FakeStore store = new();
    return (new PlanService(store), store);
  }

  /// <summary>Marks every step Done, so a completion satisfies the invariant issue #271
  ///     added (a plan cannot be completed while any step is Pending or InProgress).</summary>
  private static async Task<Plan> FinishAllStepsAsync(PlanService service, Plan plan)
  {
    Plan current = plan;
    foreach (PlanStep step in plan.Steps)
    {
      Result<Plan> updated = await service.UpdateStepAsync(current.Id, step.Position,
        s => s with { Status = PlanStepStatus.Done }, current.Version, DateTimeOffset.UtcNow,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(updated.IsSuccess, updated.Error?.Message ?? "expected the step update to succeed");
      current = updated.Value;
    }

    return current;
  }

  [Fact]
  public async Task Create_WithSteps_PersistsAll_AndRaisesCreatedEvent()
  {
    (PlanService service, _) = New();
    int raised = 0;
    service.Created += _ => raised++;
    Result<Plan> r = await service.CreateAsync("T", "G", "sess-1",
      [("a", null), ("b", "d")], DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(r.IsSuccess);
    Assert.Equal(2, r.Value.Steps.Count);
    Assert.Equal(1, raised);
  }

  [Fact]
  public async Task AddStep_BumpsVersion_AcrossRoundTrip()
  {
    (PlanService service, _) = New();
    Plan p = (await service.CreateAsync("T", "G", "s", null, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)).Value!;
    Result<Plan> r = await service.AddStepAsync(p.Id, "step", null, p.Version, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(r.IsSuccess);
    Assert.Equal(2, r.Value.Version);
    Result<Plan> fetched = await service.GetAsync(p.Id, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.Equal(2, fetched.Value!.Version);
    _ = Assert.Single(fetched.Value.Steps);
  }

  [Fact]
  public async Task Save_WithStaleVersion_FailsVersionConflict()
  {
    (PlanService service, _) = New();
    Plan p = (await service.CreateAsync("T", "G", "s", null, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)).Value!;
    _ = await service.AddStepAsync(p.Id, "one", null, p.Version, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Result<Plan> stale = await service.AddStepAsync(p.Id, "two", null, p.Version, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(stale.IsSuccess);
    Assert.Equal("VersionConflict", stale.Error.Code);
  }

  /// <summary>The linked-todo cleanup this class used to exercise is gone with the todo list
  ///     itself (issue #310): a terminal transition is a plain load-mutate-save now, so what
  ///     is left to pin is that the transition lands, that Abandoned is still ungated, and
  ///     that a stale version still fails closed.</summary>
  [Fact]
  public async Task SetStatus_OnCompleted_ReturnsTheSavedPlan()
  {
    (PlanService service, _) = New();
    Plan p = (await service.CreateAsync("T", "G", "s", [("a", null), ("b", null)],
      DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)).Value!;
    p = await FinishAllStepsAsync(service, p).ConfigureAwait(true);
    Result<Plan> r = await service.SetStatusAsync(p.Id, PlanStatus.Completed, p.Version,
      DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(r.IsSuccess);
    Assert.Equal(PlanStatus.Completed, r.Value.Status);
    Assert.Equal(p.Version + 1, r.Value.Version);
  }

  [Fact]
  public async Task SetStatus_OnAbandoned_IsUngated()
  {
    // Abandoned is the named exit every completion refusal points at, so it stays
    // reachable from a half-finished plan.
    (PlanService service, _) = New();
    Plan p = (await service.CreateAsync("T", "G", "s", [("a", null)],
      DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)).Value!;
    Result<Plan> r = await service.SetStatusAsync(p.Id, PlanStatus.Abandoned, p.Version,
      DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(r.IsSuccess);
    Assert.Equal(PlanStatus.Abandoned, r.Value.Status);
  }

  [Fact]
  public async Task SetStatus_FailingSave_FailsClosed()
  {
    (PlanService service, _) = New();
    Plan p = (await service.CreateAsync("T", "G", "s", null, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)).Value!;
    Result<Plan> r = await service.SetStatusAsync(p.Id, PlanStatus.Completed, 999,
      DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(r.IsSuccess);
    Assert.Equal("VersionConflict", r.Error.Code);
  }
}
