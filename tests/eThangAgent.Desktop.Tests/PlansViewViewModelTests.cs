using System.Collections.ObjectModel;
using eThangAgent.AgentDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.PlanDomain;
using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;

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

  private sealed class FakeStateService : IStateService
  {
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public Task<Result<string>> GetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(Values.TryGetValue(key, out string? value)
            ? Result.Success(value)
            : Result.Failure<string>(new DomainError("StateKeyNotFound", $"no key {key}")));

    public Task<Result<StateKeyValue>> SetAsync(string key, string value, int? expectedVersion, CancellationToken ct = default)
    {
      Values[key] = value;
      return Task.FromResult(Result.Success(new StateKeyValue("", key, value, 1)));
    }

    public Task<Result<string>> DeleteAsync(string key, int? expectedVersion, CancellationToken ct = default)
        => Task.FromResult(Result.Success(key));

    public Task<Result<IReadOnlyList<string>>> ListAsync(string? ns, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<string>>([.. Values.Keys]));

    public Task<Result<StateKeyValue>> AppendAsync(string key, string text, int? expectedVersion, CancellationToken ct = default)
        => Task.FromResult(Result.Success(new StateKeyValue("", key, text, 1)));

    public Task<Result<IReadOnlyList<StateSearchHit>>> SearchAsync(string query, int limit, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<StateSearchHit>>([]));

    public Task<Result<int>> DeletePrefixAsync(string nsPrefix, CancellationToken ct = default)
        => Task.FromResult(Result.Success(0));

    public Task<Result<string>> TransitionAsync(string from, string toState, string summary,
        IReadOnlyList<string> evidence, CancellationToken ct = default)
        => Task.FromResult(Result.Success(toState));

    public Task<CertificationReport> VerifyAsync(IReadOnlyList<string>? ids, CancellationToken ct = default)
        => Task.FromResult(new CertificationReport(true, false, [], []));

    public Task<CertificationReport> CheckGoalAsync(CancellationToken ct = default)
        => Task.FromResult(new CertificationReport(true, false, [], []));

    public Task<Result<IReadOnlyList<string>>> HistoryAsync(int limit, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<string>>([]));
  }

  private PlansViewViewModel Build(
      FakePlanStore store,
      FakeStateService? state = null,
      IChangeSignal? signal = null,
      IStateChangeBus? bus = null)
    => Own(new PlansViewViewModel(new PlanService(store), state, signal, bus, render => render()));

  [Fact]
  public async Task Refresh_RendersPlansAndSteps()
  {
    FakePlanStore store = new();
    _ = await store.CreateAsync(Plan.New("Issue #8", "ship the view", "sess-abcdef12", DateTimeOffset.UtcNow)
        .AddStep("build the shell", null, null).AddStep("wire realtime", null, null),
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

  [Fact]
  public async Task Refresh_NoPlansAndNoTodos_ReportsEmpty()
  {
    PlansViewViewModel vm = Build(new FakePlanStore(), new FakeStateService());

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.True(vm.IsEmpty);
    Assert.Null(vm.LoadError);
  }

  [Fact]
  public async Task Refresh_RendersTodos_OpenItemsFirst()
  {
    FakeStateService state = new();
    state.Values[TodoTool.StoreKey] = TodoDocument.Serialize(
    [
      new TodoItem(1, "done thing", TodoStatus.Completed),
      new TodoItem(2, "open thing", TodoStatus.Pending),
    ]);
    PlansViewViewModel vm = Build(new FakePlanStore(), state);

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.Equal(2, vm.Todos.Count);
    Assert.Equal("open thing", vm.Todos[0].Text);
    Assert.False(vm.Todos[0].IsDone);
    Assert.True(vm.Todos[1].IsDone);
  }

  [Fact]
  public async Task PlanCreatedEvent_RefreshesWithoutAManualCall()
  {
    // The SAME service instance the view subscribed to must raise the event: that is
    // exactly the production wiring (one PlanService per session container, shared by
    // the plan capability provider and the workbench).
    FakePlanStore store = new();
    PlanService service = new(store);
    PlansViewViewModel vm = Own(new PlansViewViewModel(service, null, null, null, render => render()));
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
  public async Task StateChangeOnTodoKey_RefreshesTheTodoList()
  {
    FakeStateService state = new();
    InProcessStateChangeBus bus = new();
    PlansViewViewModel vm = Build(new FakePlanStore(), state, signal: null, bus);
    vm.Start();
    await vm.RefreshAsync(TestContext.Current.CancellationToken);
    Assert.Empty(vm.Todos);

    state.Values[TodoTool.StoreKey] = TodoDocument.Serialize([new TodoItem(1, "from the agent", TodoStatus.Pending)]);
    bus.Publish(new StateChange("ws", TodoTool.StoreKey, StateChangeKind.Set, "agent", DateTimeOffset.UtcNow));

    // The subscription fires RefreshAsync fire-and-forget; poll until it lands.
    for (int i = 0; i < 100 && vm.Todos.Count == 0; i++)
    {
      await Task.Delay(10, TestContext.Current.CancellationToken);
    }

    _ = Assert.Single(vm.Todos);
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
        new PlanService(store), null, null, null, render => render(),
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
        new PlanService(store), null, signal, null, render => render(),
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
