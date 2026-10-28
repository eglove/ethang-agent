using System.Collections.ObjectModel;
using eThangAgent.AgentDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.PlanDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;

namespace eThangAgent.Desktop.Tests;

/// <summary>Issue #8: the workbench's Plans view must show a plan the agent creates
///     WITHOUT a manual refresh, render a corrupt row as a marker instead of blanking
///     the list (#279), and never confuse 'no plans' with 'cannot read plans'.
///     <para>The marshal callback runs inline here: these tests assert view-model state,
///     and Avalonia's dispatcher is not running in a unit test.</para></summary>
public sealed class PlansViewViewModelTests : IDisposable
{
  private readonly List<IDisposable> _disposables = [];

  public void Dispose()
  {
    foreach (IDisposable disposable in _disposables)
    {
      disposable.Dispose();
    }
  }

  /// <summary>Builds a view-model owned by this test, so disposal is guaranteed and the
  ///     poller never outlives the assertion.</summary>
  private PlansViewViewModel Own(PlansViewViewModel vm)
  {
    _disposables.Add(vm);
    return vm;
  }

  /// <summary>An in-memory plan store the test mutates directly, so a write is
  ///     indistinguishable from the agent writing one.</summary>
  private sealed class FakePlanStore : IPlanStore
  {
    public Collection<Plan> Plans { get; } = [];
    public Collection<CorruptPlanRow> Corrupt { get; } = [];
    public DomainError? ListFailure { get; set; }
    private int _next = 1;

    public Task<Result<Plan>> CreateAsync(Plan draft, CancellationToken ct = default)
    {
      Plan saved = draft with { Id = _next++, Version = 1 };
      Plans.Add(saved);
      return Task.FromResult(Result.Success(saved));
    }

    public Task<Result<Plan>> GetAsync(int id, CancellationToken ct = default) =>
        Task.FromResult(Plans.FirstOrDefault(p => p.Id == id) is { } plan
            ? Result.Success(plan)
            : Result.Failure<Plan>(new DomainError("PlanNotFound", $"no plan #{id}")));

    public Task<Result<PlanCatalog>> ListAsync(PlanStatus? status, CancellationToken ct = default) =>
        Task.FromResult(ListFailure is not null
            ? Result.Failure<PlanCatalog>(ListFailure)
            : Result.Success(new PlanCatalog(
                [.. Plans.Where(p => status is null || p.Status == status)], [.. Corrupt])));

    public Task<Result<Plan>> SaveAsync(Plan plan, int expectedVersion, CancellationToken ct = default)
        => Task.FromResult(Result.Success(plan));
  }

  private PlansViewViewModel Build(FakePlanStore store, IChangeSignal? signal = null)
    => Own(new PlansViewViewModel(new PlanService(store), signal, render => render()));

  [Fact]
  public async Task Refresh_RendersPlansAndSteps()
  {
    FakePlanStore store = new();
    _ = await store.CreateAsync(Plan.New("Issue #8", "ship the view", "sess-abcdef12", DateTimeOffset.UtcNow)
        .AddStep("build the shell", null).AddStep("wire realtime", null),
        TestContext.Current.CancellationToken);
    PlansViewViewModel vm = Build(store);

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    PlanCardViewModel card = Assert.Single(vm.Plans);
    Assert.Contains("Issue #8", card.Title, StringComparison.Ordinal);
    Assert.Equal(2, card.Steps.Count);
    Assert.Equal("\u25CB", card.Steps[0].Marker);
    Assert.False(vm.IsEmpty);
    Assert.Null(vm.LoadError);
  }

  [Fact]
  public async Task Refresh_RendersSkippedStepWithItsOwnMarker_NotThePendingCircle()
  {
    FakePlanStore store = new();
    _ = await store.CreateAsync(Plan.New("#271", "g", "sess-abcdef12", DateTimeOffset.UtcNow)
        .AddStep("done", null).AddStep("skipped", null).AddStep("pending", null)
        .UpdateStep(1, s => s with { Status = PlanStepStatus.Done })
        .UpdateStep(2, s => s with { Status = PlanStepStatus.Skipped }),
        TestContext.Current.CancellationToken);
    PlansViewViewModel vm = Build(store);

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    PlanCardViewModel card = Assert.Single(vm.Plans);
    Assert.Equal("\u2713", card.Steps[0].Marker);
    Assert.Equal("\u2014", card.Steps[1].Marker);
    Assert.Equal("\u25CB", card.Steps[2].Marker);
    // Skipped settles a step the way Done does; it is not outstanding work.
    Assert.True(card.Steps[1].IsDone);
    Assert.False(card.Steps[2].IsDone);
  }

  [Fact]
  public async Task Refresh_RendersCorruptRowsAsMarkers_InsteadOfBlankingTheList()
  {
    FakePlanStore store = new();
    _ = await store.CreateAsync(Plan.New("healthy", "g", "s", DateTimeOffset.UtcNow),
        TestContext.Current.CancellationToken);
    store.Corrupt.Add(new CorruptPlanRow(99, "unknown step status 'Completed'"));
    PlansViewViewModel vm = Build(store);

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.Equal(2, vm.Plans.Count);
    PlanCardViewModel corrupt = vm.Plans.Single(c => c.IsCorrupt);
    Assert.Equal(99, corrupt.PlanId);
    Assert.Contains("Completed", corrupt.CorruptReason, StringComparison.Ordinal);
    Assert.True(vm.HasCorruptRows);
    // The healthy plan still renders: one bad row never blanks the catalog (#279).
    _ = Assert.Single(vm.Plans, c => !c.IsCorrupt);
  }

  [Fact]
  public async Task Refresh_LoadFailure_RendersAnError_NotAnEmptyState()
  {
    FakePlanStore store = new()
    {
      ListFailure = new DomainError("StorageUnavailable", "the database is locked"),
    };
    PlansViewViewModel vm = Build(store);

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.NotNull(vm.LoadError);
    Assert.Contains("database is locked", vm.LoadError, StringComparison.Ordinal);
    Assert.Equal("error", vm.StatusLine);
  }

  /// <summary>Issue #310: the todo list is retired, so the view's empty state is a
  ///     question about plans ALONE. The todo rows this test used to seed are gone with
  ///     the tool that produced them.</summary>
  [Fact]
  public async Task Refresh_NoPlans_ReportsEmpty()
  {
    PlansViewViewModel vm = Build(new FakePlanStore());

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.True(vm.IsEmpty);
    Assert.Null(vm.LoadError);
  }

  [Fact]
  public async Task PlanCreatedEvent_RefreshesWithoutAManualCall()
  {
    // The SAME service instance the view subscribed to must raise the event: that is
    // exactly the production wiring (one PlanService per session container, shared by
    // the plan capability provider and the workbench).
    FakePlanStore store = new();
    PlanService service = new(store);
    PlansViewViewModel vm = Own(new PlansViewViewModel(service, null, render => render()));
    vm.Start();
    await vm.RefreshAsync(TestContext.Current.CancellationToken);
    Assert.True(vm.IsEmpty);

    // The agent creates a plan through the service; the view must pick it up from the
    // event alone - no manual refresh.
    Result<Plan> created = await service.CreateAsync("new work", "g", "s", null,
        DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
    Assert.True(created.IsSuccess, created.Error?.Message ?? "expected success");

    for (int i = 0; i < 100 && vm.Plans.Count == 0; i++)
    {
      await Task.Delay(10, TestContext.Current.CancellationToken);
    }

    _ = Assert.Single(vm.Plans);
  }

  [Fact]
  public async Task PlanChangedEvent_RefreshesWithoutAManualCall()
  {
    // The INSTANT path (issue #8 follow-up). A plan write in production is dispatched by
    // the model in the ChildHost process, so the app container's PlanService events never
    // fire - the write reaches this process as a PlanChangedEvent on the session's event
    // stream (the same stream a parent agent watches with wakeOn=plan). The view must
    // re-read on that fact alone: no poll interval, no manual refresh.
    FakePlanStore store = new();
    FakeEvents events = new();
    PlansViewViewModel vm = Own(new PlansViewViewModel(
        new PlanService(store), null, render => render(),
        pollInterval: TimeSpan.FromHours(1), events: events));
    vm.Start();
    await vm.RefreshAsync(TestContext.Current.CancellationToken);
    Assert.True(vm.IsEmpty);

    // The agent (in the host) writes a plan; the fact arrives on the event stream.
    _ = await store.CreateAsync(Plan.New("written by the host", "g", "s", DateTimeOffset.UtcNow),
        TestContext.Current.CancellationToken);
    events.Publish(new PlanChangedEvent(new AgentId(Guid.NewGuid()), DateTimeOffset.UtcNow,
        1, "created", null));

    for (int i = 0; i < 200 && vm.Plans.Count == 0; i++)
    {
      await Task.Delay(10, TestContext.Current.CancellationToken);
    }

    _ = Assert.Single(vm.Plans);
  }

  /// <summary>A minimal event stream the test drives: the view subscribes, the test
  ///     publishes, and no poller is involved.</summary>
  private sealed class FakeEvents : IAgentEvents
  {
    private readonly List<IAgentEventSubscriber> _subscribers = [];

    public IDisposable Subscribe(IAgentEventSubscriber subscriber)
    {
      _subscribers.Add(subscriber);
      return new Lease(this, subscriber);
    }

    public void Publish(ChildEvent evt)
    {
      foreach (IAgentEventSubscriber subscriber in _subscribers.ToArray())
      {
        subscriber.OnEvent(evt);
      }
    }

    private sealed class Lease(FakeEvents owner, IAgentEventSubscriber subscriber) : IDisposable
    {
      public void Dispose() => owner._subscribers.Remove(subscriber);
    }
  }

  [Fact]
  public async Task ChangeSignal_MovingVersion_TriggersARefresh()
  {
    FakePlanStore store = new();
    CountingSignal signal = new();
    PlansViewViewModel vm = Own(new PlansViewViewModel(
        new PlanService(store), signal, render => render(),
        TimeSpan.FromMilliseconds(5)));
    vm.Start();
    await vm.RefreshAsync(TestContext.Current.CancellationToken);
    Assert.Empty(vm.Plans);

    // Another process commits: the signal moves and the view re-reads on its own.
    _ = await store.CreateAsync(Plan.New("written by the host", "g", "s", DateTimeOffset.UtcNow),
        TestContext.Current.CancellationToken);
    signal.Bump();

    for (int i = 0; i < 200 && vm.Plans.Count == 0; i++)
    {
      await Task.Delay(10, TestContext.Current.CancellationToken);
    }

    _ = Assert.Single(vm.Plans);
  }

  /// <summary>A change signal the test drives: the poller sees a new value only when
  ///     the test says another process committed.</summary>
  private sealed class CountingSignal : IChangeSignal
  {
    private long _value = 1;

    public long Read() => Interlocked.Read(ref _value);

    public void Bump() => Interlocked.Increment(ref _value);
  }
}
