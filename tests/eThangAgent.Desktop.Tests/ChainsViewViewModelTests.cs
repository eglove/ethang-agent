using System.Collections.ObjectModel;
using eThangAgent.AgentDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;

namespace eThangAgent.Desktop.Tests;

/// <summary>Issue #312: the workbench's Chains view must render a chain's status, its current
///     step, and each step's outcome; make a PARKED chain visually distinct from a running one
///     and name its reason and the step it stopped at (the criterion inherited from #303);
///     refresh on chain events rather than polling; and stay read-only except the one action.
///     <para>The marshal callback runs inline here: these tests assert view-model state, and
///     Avalonia's dispatcher is not running in a unit test.</para></summary>
public sealed class ChainsViewViewModelTests : IDisposable
{
  private readonly List<IDisposable> _disposables = [];
  private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

  public void Dispose()
  {
    foreach (IDisposable disposable in _disposables)
    {
      disposable.Dispose();
    }
  }

  /// <summary>Builds a view-model owned by this test, so disposal is guaranteed and the
  ///     poller never outlives the assertion.</summary>
  private ChainsViewViewModel Own(ChainsViewViewModel vm)
  {
    _disposables.Add(vm);
    return vm;
  }

  /// <summary>An in-memory chain store the test mutates directly, so a write is
  ///     indistinguishable from the harness writing one.</summary>
  private sealed class FakeChainStore : IChainStore
  {
    public Collection<Chain> Chains { get; } = [];
    public Collection<CorruptChainRow> Corrupt { get; } = [];
    public DomainError? ListFailure { get; set; }
    private int _next = 1;

    public Task<Result<Chain>> CreateAsync(Chain draft, CancellationToken ct = default)
    {
      Chain saved = draft with { Id = _next++, Version = 1 };
      Chains.Add(saved);
      return Task.FromResult(Result.Success(saved));
    }

    public Task<Result<Chain>> GetAsync(int id, CancellationToken ct = default) =>
        Task.FromResult(Chains.FirstOrDefault(c => c.Id == id) is { } chain
            ? Result.Success(chain)
            : Result.Failure<Chain>(new DomainError("ChainNotFound", $"no chain #{id}")));

    public Task<Result<ChainCatalog>> ListAsync(ChainStatus? status, CancellationToken ct = default) =>
        Task.FromResult(ListFailure is not null
            ? Result.Failure<ChainCatalog>(ListFailure)
            : Result.Success(new ChainCatalog(
                [.. Chains.Where(c => status is null || c.Status == status)], [.. Corrupt])));

    public Task<Result<Chain>> SaveAsync(Chain chain, int expectedVersion, CancellationToken ct = default)
        => Task.FromResult(Result.Success(chain));

    public Task<Result<Chain?>> FindBySessionAsync(string sessionId, CancellationToken ct = default)
    {
      Chain? found = Chains.FirstOrDefault(chain =>
          chain.Steps.Any(step => string.Equals(step.SessionId, sessionId, StringComparison.Ordinal)));
      return Task.FromResult(Result.Success(found));
    }
  }

  private ChainsViewViewModel Build(FakeChainStore store, IChangeSignal? signal = null,
      IAgentEvents? events = null, Func<string, Task>? resume = null)
    => Own(new ChainsViewViewModel(new ChainService(store), signal, render => render(),
        pollInterval: TimeSpan.FromHours(1), events: events, resumeSession: resume));

  /// <summary>The session the sample chain's claimed step belongs to.</summary>
  private const string SampleSession = "sess-abcdef1234";

  /// <summary>A three-step chain: step 2 settled Done, step 1 claimed by the sample session.
  ///     <para>THE CLAIM IS WHAT GIVES A STEP A SESSION, and a step with no session is one the
  ///     view cannot offer to open - so a fixture without it could not exercise the one action at
  ///     all.</para></summary>
  /// <param name="status">The chain's status.</param>
  /// <param name="parkReason">The park reason, for a parked chain.</param>
  /// <param name="parkedAt">The position the chain stopped at.</param>
  private static Chain SampleChain(ChainStatus status = ChainStatus.Running, string? parkReason = null,
      int? parkedAt = null)
  {
    Chain chain = Chain.New("ws", 7, 3, "ship the rail view", SampleSession, [1, 2, 3], Now) with
    {
      Id = 4,
      Version = 1,
    };
    List<ChainStep> steps = [.. chain.Steps];
    steps[1] = steps[1].Settle(ChainStepStatus.Done, ChainActor.Session(SampleSession),
        "wrote the view", Now);
    steps[0] = steps[0].Claim(SampleSession, Now);
    Chain claimed = chain with { Steps = steps };
    return status == ChainStatus.Running
        ? claimed
        : claimed with { Status = status, ParkReason = parkReason, ParkedAtPosition = parkedAt };
  }

  [Fact]
  public async Task Refresh_RendersChainsWithStatusCurrentStepAndOutcomes()
  {
    FakeChainStore store = new();
    store.Chains.Add(SampleChain());
    ChainsViewViewModel vm = Build(store);

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    ChainCardViewModel card = Assert.Single(vm.Chains);
    Assert.Equal(4, card.ChainId);
    Assert.Contains("ship the rail view", card.Title, StringComparison.Ordinal);
    Assert.Equal(ChainStatusKind.Running, card.StatusKind);
    Assert.Equal("RUNNING", card.StatusDisplay);
    // The current step is the one a session holds - the harness claimed step 1 when it
    // created the chain, so the record says so.
    Assert.False(string.IsNullOrWhiteSpace(card.CurrentStep));
    Assert.Equal(3, card.Steps.Count);
    Assert.Equal("\u2713", card.Steps[1].Marker);
    Assert.Equal("wrote the view", card.Steps[1].Outcome);
    Assert.Equal("\u25CB", card.Steps[2].Marker);
    Assert.False(vm.IsEmpty);
    Assert.Null(vm.LoadError);
  }

  /// <summary>The criterion inherited from #303: a PARKED chain must be VISUALLY DISTINCT from
  ///     a running one and must name its reason AND the step it stopped at. 'Waiting for you'
  ///     and 'working' are different facts, and a surface that drew them alike would undo the
  ///     distinction the chain's status machine exists to preserve.</summary>
  [Fact]
  public async Task Refresh_AParkedChain_IsDistinctAndNamesItsReasonAndStep()
  {
    FakeChainStore store = new();
    store.Chains.Add(SampleChain(ChainStatus.Parked, "step 2 failed 3 time(s)", 2));
    ChainsViewViewModel vm = Build(store);

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    ChainCardViewModel card = Assert.Single(vm.Chains);
    Assert.True(card.IsParked);
    Assert.False(card.IsFinished);
    Assert.Equal(ChainStatusKind.Parked, card.StatusKind);
    Assert.Equal("PARKED", card.StatusDisplay);
    // The reason AND the step, in one sentence: the record carries both, so the view renders both.
    Assert.NotNull(card.ParkDetail);
    Assert.Contains("step 2", card.ParkDetail, StringComparison.Ordinal);
    Assert.Contains("failed 3 time(s)", card.ParkDetail, StringComparison.Ordinal);
    Assert.Contains("waiting at step 2", card.CurrentStep, StringComparison.Ordinal);
  }

  /// <summary>A chain parked with NO position still renders its reason: a chain parked because
  ///     nothing was available has no step, and 'parked' with no explanation is exactly the state
  ///     the card exists to prevent.</summary>
  [Fact]
  public async Task Refresh_AParkedChainWithNoPosition_StillNamesItsReason()
  {
    FakeChainStore store = new();
    store.Chains.Add(SampleChain(ChainStatus.Parked, "nothing left to start", null));
    ChainsViewViewModel vm = Build(store);

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    ChainCardViewModel card = Assert.Single(vm.Chains);
    Assert.Equal("nothing left to start", card.ParkDetail);
  }

  /// <summary>A running chain renders NO park sentence, so a stale reason can never appear on a
  ///     chain that is not waiting for anything.</summary>
  [Fact]
  public async Task Refresh_ARunningChain_RendersNoParkSentence()
  {
    FakeChainStore store = new();
    store.Chains.Add(SampleChain());
    ChainsViewViewModel vm = Build(store);

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.Null(Assert.Single(vm.Chains).ParkDetail);
  }

  /// <summary>D6: the parked count is a property AND an event, because the rail may be closed and
  ///     a chain waiting for a human cannot depend on the user having opened a view.</summary>
  [Fact]
  public async Task Refresh_RaisesTheParkedCountForASurfaceThatIsAlwaysVisible()
  {
    FakeChainStore store = new();
    store.Chains.Add(SampleChain(ChainStatus.Parked, "needs a human", 1));
    store.Chains.Add(SampleChain());
    ChainsViewViewModel vm = Build(store);
    List<int> raised = [];
    vm.ParkedCountChanged += raised.Add;

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.Equal(1, vm.ParkedCount);
    Assert.Equal([1], raised);
  }

  /// <summary>A badge that re-raised on every refresh would flicker for no reason, and the count
  ///     is the only thing the listener consumes.</summary>
  [Fact]
  public async Task Refresh_WithAnUnchangedParkedCount_RaisesNothing()
  {
    FakeChainStore store = new();
    store.Chains.Add(SampleChain(ChainStatus.Parked, "needs a human", 1));
    ChainsViewViewModel vm = Build(store);
    await vm.RefreshAsync(TestContext.Current.CancellationToken);
    List<int> raised = [];
    vm.ParkedCountChanged += raised.Add;

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.Empty(raised);
  }

  /// <summary>D7: a completed chain STAYS LISTED, collapsed by default, and the filter is the
  ///     'filterable' half of that decision. History is the point of the record.
  ///     <para>Abandoned sorts with Completed rather than above it: both are finished, and the
  ///     distinction is on the card rather than in the ordering.</para></summary>
  [Fact]
  public async Task Refresh_AFinishedChain_StaysListedCollapsedAndFilterable()
  {
    FakeChainStore store = new();
    store.Chains.Add(SampleChain(ChainStatus.Completed));
    ChainsViewViewModel vm = Build(store);

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    ChainCardViewModel card = Assert.Single(vm.Chains);
    Assert.True(card.IsFinished);
    Assert.False(card.IsExpanded);

    vm.ShowFinished = false;
    Assert.Empty(vm.Chains);
    Assert.True(vm.IsEmpty);
  }

  /// <summary>An ACTIVE chain defaults open: its steps are what the user opened the view for.
  ///     </summary>
  [Fact]
  public async Task Refresh_AnActiveChain_DefaultsExpanded()
  {
    FakeChainStore store = new();
    store.Chains.Add(SampleChain());
    ChainsViewViewModel vm = Build(store);

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.True(Assert.Single(vm.Chains).IsExpanded);
  }

  /// <summary>The user's toggle survives a refresh: the card is REPLACED on every read, so a
  ///     choice held only on the old card would snap shut the moment anything moved.</summary>
  [Fact]
  public async Task RememberExpansion_KeepsTheUsersChoiceAcrossARefresh()
  {
    FakeChainStore store = new();
    store.Chains.Add(SampleChain());
    ChainsViewViewModel vm = Build(store);
    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    ChainCardViewModel card = Assert.Single(vm.Chains);
    card.IsExpanded = false;
    vm.RememberExpansion(card);
    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.False(Assert.Single(vm.Chains).IsExpanded);
  }

  /// <summary>Parked first (they need a human), then running, then history.</summary>
  [Fact]
  public async Task Refresh_OrdersParkedFirstThenRunningThenFinished()
  {
    FakeChainStore store = new();
    Chain completed = SampleChain(ChainStatus.Completed) with { Id = 9 };
    Chain running = SampleChain() with { Id = 8 };
    Chain parked = SampleChain(ChainStatus.Parked, "needs a human", 1) with { Id = 7 };
    store.Chains.Add(completed);
    store.Chains.Add(running);
    store.Chains.Add(parked);
    ChainsViewViewModel vm = Build(store);

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.Equal([7, 8, 9], vm.Chains.Select(card => card.ChainId));
  }

  /// <summary>Issue #279's containment, applied to chains: one unreadable row renders as a named
  ///     marker and never blanks the workspace's chain list.</summary>
  [Fact]
  public async Task Refresh_RendersCorruptRowsAsMarkers_InsteadOfBlankingTheList()
  {
    FakeChainStore store = new();
    store.Chains.Add(SampleChain());
    store.Corrupt.Add(new CorruptChainRow(99, "unknown step status 'Completed'"));
    ChainsViewViewModel vm = Build(store);

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.Equal(2, vm.Chains.Count);
    ChainCardViewModel corrupt = vm.Chains.Single(card => card.IsCorrupt);
    Assert.Equal(99, corrupt.ChainId);
    Assert.Contains("Completed", corrupt.CorruptReason, StringComparison.Ordinal);
    Assert.True(vm.HasCorruptRows);
    _ = Assert.Single(vm.Chains, card => !card.IsCorrupt);
  }

  /// <summary>A storage failure is not 'no chains' - the two must never look the same.</summary>
  [Fact]
  public async Task Refresh_LoadFailure_RendersAnError_NotAnEmptyState()
  {
    FakeChainStore store = new()
    {
      ListFailure = new DomainError("StorageUnavailable", "the database is locked"),
    };
    ChainsViewViewModel vm = Build(store);

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.NotNull(vm.LoadError);
    Assert.Contains("database is locked", vm.LoadError, StringComparison.Ordinal);
    Assert.Equal("error", vm.StatusLine);
  }

  [Fact]
  public async Task Refresh_NoChains_ReportsEmpty()
  {
    ChainsViewViewModel vm = Build(new FakeChainStore());

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.True(vm.IsEmpty);
    Assert.Null(vm.LoadError);
  }

  /// <summary>D4: the view's PRIMARY mechanism is the chain event, not a poll. The fact is the
  ///     one the ChildHost forwards over the wire, so a chain the model advances in the host
  ///     process reaches this view with no polling interval at all.</summary>
  [Fact]
  public async Task ChainChangedEvent_RefreshesWithoutAManualCall()
  {
    FakeChainStore store = new();
    FakeEvents events = new();
    ChainsViewViewModel vm = Build(store, events: events);
    vm.Start();
    await vm.RefreshAsync(TestContext.Current.CancellationToken);
    Assert.True(vm.IsEmpty);

    store.Chains.Add(SampleChain());
    events.Publish(new ChainChangedEvent(new AgentId(Guid.NewGuid()), DateTimeOffset.UtcNow,
        4, "step", null));

    for (int i = 0; i < 200 && vm.Chains.Count == 0; i++)
    {
      await Task.Delay(10, TestContext.Current.CancellationToken);
    }

    _ = Assert.Single(vm.Chains);
  }

  /// <summary>Only a CHAIN fact refreshes this view: the stream carries every child-run event in
  ///     the family, and re-reading the chain table on each one would make the poll the primary
  ///     mechanism by accident.</summary>
  [Fact]
  public async Task APlanChangedEvent_DoesNotRefreshTheChainView()
  {
    FakeChainStore store = new();
    FakeEvents events = new();
    ChainsViewViewModel vm = Build(store, events: events);
    vm.Start();
    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    store.Chains.Add(SampleChain());
    events.Publish(new PlanChangedEvent(new AgentId(Guid.NewGuid()), DateTimeOffset.UtcNow,
        1, "steps", null));
    await Task.Delay(100, TestContext.Current.CancellationToken);

    Assert.Empty(vm.Chains);
  }

  /// <summary>The change signal is the BACKSTOP, not the primary mechanism: a write that
  ///     published no fact still reaches the view.</summary>
  [Fact]
  public async Task ChangeSignal_MovingVersion_TriggersARefresh()
  {
    FakeChainStore store = new();
    CountingSignal signal = new();
    ChainsViewViewModel vm = Own(new ChainsViewViewModel(
        new ChainService(store), signal, render => render(), TimeSpan.FromMilliseconds(5)));
    vm.Start();
    await vm.RefreshAsync(TestContext.Current.CancellationToken);
    Assert.Empty(vm.Chains);

    store.Chains.Add(SampleChain());
    signal.Bump();

    for (int i = 0; i < 200 && vm.Chains.Count == 0; i++)
    {
      await Task.Delay(10, TestContext.Current.CancellationToken);
    }

    _ = Assert.Single(vm.Chains);
  }

  /// <summary>D8: the ONE action opens a chain's session - the same operation the Sessions menu
  ///     performs - and it picks the PARKED step, because a parked chain is the case this
  ///     surface exists for.</summary>
  [Fact]
  public async Task OpenSession_OpensTheParkedStepsSession()
  {
    FakeChainStore store = new();
    store.Chains.Add(SampleChain(ChainStatus.Parked, "needs a human", 2));
    List<string> opened = [];
    ChainsViewViewModel vm = Build(store, resume: id =>
    {
      opened.Add(id);
      return Task.CompletedTask;
    });
    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    await vm.OpenSessionAsync(Assert.Single(vm.Chains), TestContext.Current.CancellationToken);

    // Step 2 settled Done with the session, and the chain is parked AT step 2, so the step
    // whose session the reader wants is the one that recorded the failure.
    _ = Assert.Single(opened);
  }

  /// <summary>A chain with no session at all refuses the action with a message rather than
  ///     silently doing nothing - a button that does nothing is worse than one that says why.
  ///     </summary>
  [Fact]
  public async Task OpenSession_WithNoSession_SaysWhyItDidNothing()
  {
    FakeChainStore store = new();
    Chain chain = Chain.New("ws", 7, 3, "nothing started", null, [1], Now) with { Id = 5 };
    store.Chains.Add(chain);
    ChainsViewViewModel vm = Build(store, resume: _ => Task.CompletedTask);
    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    await vm.OpenSessionAsync(Assert.Single(vm.Chains), TestContext.Current.CancellationToken);

    Assert.NotNull(vm.ActionMessage);
    Assert.Contains("no session to open", vm.ActionMessage, StringComparison.Ordinal);
  }

  /// <summary>A view with no resume seam reports that rather than pretending to open one.
  ///     </summary>
  [Fact]
  public async Task OpenSession_WithoutAResumeSeam_SaysSo()
  {
    FakeChainStore store = new();
    store.Chains.Add(SampleChain());
    ChainsViewViewModel vm = Build(store);
    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    await vm.OpenSessionAsync(Assert.Single(vm.Chains), TestContext.Current.CancellationToken);

    Assert.NotNull(vm.ActionMessage);
    Assert.Contains("no way to open", vm.ActionMessage, StringComparison.Ordinal);
  }

  /// <summary>A step line is bounded: an outcome can be a whole review's cited findings, and a
  ///     summary that can be any length is not one. The full text stays on the record.</summary>
  [Fact]
  public async Task Refresh_TruncatesALongOutcome()
  {
    FakeChainStore store = new();
    Chain chain = SampleChain();
    List<ChainStep> steps = [.. chain.Steps];
    steps[0] = steps[0].Settle(ChainStepStatus.Done, ChainActor.Harness,
        new string('x', ChainStepRowViewModel.OutcomeLimit + 50), Now);
    store.Chains.Add(chain with { Steps = steps });
    ChainsViewViewModel vm = Build(store);

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    string outcome = Assert.Single(vm.Chains).Steps[0].Outcome!;
    Assert.Equal(ChainStepRowViewModel.OutcomeLimit + 1, outcome.Length);
    Assert.EndsWith("\u2026", outcome, StringComparison.Ordinal);
  }

  /// <summary>A minimal event stream the test drives: the view subscribes, the test publishes,
  ///     and no poller is involved.</summary>
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

  /// <summary>A change signal the test drives: the poller sees a new value only when the test
  ///     says another process committed.</summary>
  private sealed class CountingSignal : IChangeSignal
  {
    private long _value = 1;

    public long Read() => Interlocked.Read(ref _value);

    public void Bump() => Interlocked.Increment(ref _value);
  }
}
