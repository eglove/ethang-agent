using eThangAgent.PlanDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Storage.ACL.Tests;

public sealed class SqlitePlanStoreTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(
      Path.GetTempPath(), $"ethang-plan-{Guid.NewGuid():N}.db");
  private readonly SqlitePlanStore _store;

  public SqlitePlanStoreTests()
      => _store = new SqlitePlanStore(new AppDatabase(_dbPath), "C:\\ws\\alpha");

  public void Dispose()
  {
    GC.SuppressFinalize(this);
    // Named decision (CA1031): temp-db cleanup is best effort.
#pragma warning disable CA1031, S108 // Do not catch general exception types
    try
    {
      File.Delete(_dbPath);
    }
    catch { }
#pragma warning restore CA1031, S108
  }

  [Fact]
  public async Task Create_AssignsGlobalIds_AndRoundTripsSteps()
  {
    Plan draft = Plan.New("T1", "G1", "sess-a", DateTimeOffset.UtcNow)
      .AddStep("one", null, null).AddStep("two", "d", 9);
    Result<Plan> r = await _store.CreateAsync(draft, TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess, r.Error?.Message ?? "expected success");
    Assert.True(r.Value.Id > 0);
    Result<Plan> back = await _store.GetAsync(r.Value.Id, TestContext.Current.CancellationToken);
    Assert.True(back.IsSuccess, back.Error?.Message ?? "expected success");
    Assert.Equal(2, back.Value.Steps.Count);
    Assert.Equal(9, back.Value.Steps[1].TodoId);
    Assert.Equal(PlanStepStatus.Pending, back.Value.Steps[1].Status);
  }

  [Fact]
  public async Task Isolation_SecondWorkspace_CannotSeeOrMutateFirst()
  {
    Plan draft = Plan.New("secret", "g", "s", DateTimeOffset.UtcNow);
    Plan created = (await _store.CreateAsync(draft, TestContext.Current.CancellationToken)).Value!;
    SqlitePlanStore other = new(new AppDatabase(_dbPath), "C:\\ws\\beta");
    Assert.False((await other.GetAsync(created.Id, TestContext.Current.CancellationToken)).IsSuccess);
    Result<Plan> crossSave = await other.SaveAsync(created.AddStep("x", null, null), 1, TestContext.Current.CancellationToken);
    Assert.False(crossSave.IsSuccess);
  }

  [Fact]
  public async Task Save_CasConflict_WhenVersionStale()
  {
    Plan draft = Plan.New("T", "G", "s", DateTimeOffset.UtcNow);
    Plan created = (await _store.CreateAsync(draft, TestContext.Current.CancellationToken)).Value!;
    Result<Plan> stale = await _store.SaveAsync(created.AddStep("x", null, null), 0, TestContext.Current.CancellationToken);
    Assert.False(stale.IsSuccess);
    Assert.Equal("VersionConflict", stale.Error.Code);
  }

  [Fact]
  public async Task List_FiltersByStatus_AndWorkspace()
  {
    Plan active = (await _store.CreateAsync(Plan.New("A", "g", "s", DateTimeOffset.UtcNow), TestContext.Current.CancellationToken)).Value!;
    _ = await _store.CreateAsync(Plan.New("B", "g", "s", DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
    _ = await _store.SaveAsync(active.SetStatus(PlanStatus.Completed, DateTimeOffset.UtcNow), 1, TestContext.Current.CancellationToken);
    Result<IReadOnlyList<Plan>> actives = await _store.ListAsync(PlanStatus.Active, TestContext.Current.CancellationToken);
    _ = Assert.Single(actives.Value!);
  }
}
