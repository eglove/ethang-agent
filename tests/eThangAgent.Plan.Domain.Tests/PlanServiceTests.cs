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
    public Task<Result<IReadOnlyList<Plan>>> ListAsync(PlanStatus? status, CancellationToken ct = default) =>
      Task.FromResult(Result.Success<IReadOnlyList<Plan>>([.. _plans.Values
        .Where(p => status is null || p.Status == status)]));
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

  [Fact]
  public async Task Create_WithSteps_PersistsAll_AndRaisesCreatedEvent()
  {
    (PlanService service, _) = New();
    int raised = 0;
    service.Created += _ => raised++;
    Result<Plan> r = await service.CreateAsync("T", "G", "sess-1",
      [("a", null, null), ("b", "d", 4)], DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(r.IsSuccess);
    Assert.Equal(2, r.Value.Steps.Count);
    Assert.Equal(1, raised);
  }

  [Fact]
  public async Task AddStep_BumpsVersion_AcrossRoundTrip()
  {
    (PlanService service, _) = New();
    Plan p = (await service.CreateAsync("T", "G", "s", null, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)).Value!;
    Result<Plan> r = await service.AddStepAsync(p.Id, "step", null, null, p.Version, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
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
    _ = await service.AddStepAsync(p.Id, "one", null, null, p.Version, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Result<Plan> stale = await service.AddStepAsync(p.Id, "two", null, null, p.Version, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(stale.IsSuccess);
    Assert.Equal("VersionConflict", stale.Error.Code);
  }

  [Fact]
  public async Task SetStatus_OnTerminal_FailsInvalidTransition()
  {
    (PlanService service, _) = New();
    Plan p = (await service.CreateAsync("T", "G", "s", null, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)).Value!;
    _ = await service.SetStatusAsync(p.Id, PlanStatus.Completed, p.Version, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Result<Plan> again = await service.SetStatusAsync(p.Id, PlanStatus.Active, p.Version + 1, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(again.IsSuccess);
    Assert.Equal("InvalidTransition", again.Error.Code);
  }
}
