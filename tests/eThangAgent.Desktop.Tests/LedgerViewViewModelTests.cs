using eThangAgent.AgentDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.Tests;

/// <summary>Issue #318: the workbench's Scientific-method ledger view must list the workspace's
///     ledgers with each one's open-prediction count (D2), render a selected ledger's records in
///     order with a refuted verdict visually distinct (D3), refresh on state events rather than
///     polling (D4), render the stored lines faithfully rather than re-formatting them (D5), and
///     stay read-only (D1).
///     <para>The marshal callback runs inline here: these tests assert view-model state, and
///     Avalonia's dispatcher is not running in a unit test.</para></summary>
public sealed class LedgerViewViewModelTests : IDisposable
{
  private readonly List<IDisposable> _disposables = [];

  /// <summary>A ledger whose loop is mid-flight: two hypotheses, two predictions (one judged,
  ///     one still open), one verdict, and one observation.
  ///     <para>THE SHAPES ARE THE EXPERIMENT TOOL'S OWN, written by hand here because a fixture
  ///     that invented a different line shape would prove the view renders the fixture rather
  ///     than the ledger.</para></summary>
  private const string SampleLedger =
      "hypothesis 1: the watcher reports writes, not changes\n" +
      "prediction 1 (hypothesis 1): a no-op rewrite fires the watcher \u2014 falsified by: touch f.txt\n" +
      "verdict 1 (prediction 1): confirmed \u2014 two Changed events, bytes identical\n" +
      "hypothesis 2: a snapshot sees only real changes\n" +
      "prediction 2 (hypothesis 2): hashing costs under a second \u2014 falsified by: time it\n" +
      "observation 1: 15,013 events for one build burst";

  public void Dispose()
  {
    foreach (IDisposable disposable in _disposables)
    {
      disposable.Dispose();
    }
  }

  private LedgerViewViewModel Own(LedgerViewViewModel vm)
  {
    _disposables.Add(vm);
    return vm;
  }

  /// <summary>An in-memory ledger access the test mutates directly, so a write is
  ///     indistinguishable from the host appending one.</summary>
  private sealed class FakeLedgerAccess : IExperimentLedgerAccess
  {
    public Dictionary<string, string> Ledgers { get; } = new(StringComparer.Ordinal);

    public DomainError? ListFailure { get; set; }

    public Task<Result<IReadOnlyList<LedgerSummary>>> ListAsync(CancellationToken ct = default)
    {
      if (ListFailure is not null)
      {
        return Task.FromResult(Result.Failure<IReadOnlyList<LedgerSummary>>(ListFailure));
      }

      IReadOnlyList<LedgerSummary> summaries =
      [
          .. Ledgers.Select(entry =>
          {
            ExperimentLedgerSnapshot snapshot = ExperimentLedgerReader.Read(entry.Value);
            return new LedgerSummary(entry.Key, ExperimentLedgerKey.SlugOf(entry.Key),
                snapshot.RecordCount, snapshot.OpenPredictions);
          }),
      ];
      return Task.FromResult(Result.Success(summaries));
    }

    public Task<Result<ExperimentLedgerSnapshot>> ReadAsync(string key, CancellationToken ct = default)
    {
      return Task.FromResult(Result.Success(Ledgers.TryGetValue(key, out string? value)
          ? ExperimentLedgerReader.Read(value)
          : ExperimentLedgerSnapshot.Empty));
    }
  }

  private LedgerViewViewModel Build(FakeLedgerAccess access, IChangeSignal? signal = null,
      IAgentEvents? events = null)
      => Own(new LedgerViewViewModel(access, signal, render => render(),
          pollInterval: TimeSpan.FromHours(1), events: events));

  /// <summary>D2: the list opens on the LEDGERS with each one's open-prediction count - the
  ///     number that tells a user whether the loop is being run honestly.</summary>
  [Fact]
  public async Task Refresh_ListsLedgersWithTheirOpenPredictionCounts()
  {
    FakeLedgerAccess access = new();
    access.Ledgers["sci.318-ledger-view/ledger"] = SampleLedger;
    LedgerViewViewModel vm = Build(access);

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    LedgerCardViewModel card = Assert.Single(vm.Ledgers);
    Assert.Equal("318-ledger-view", card.Slug);
    // Two predictions, one judged: exactly one is still open, and that is the number the finish
    // gate reads too.
    Assert.Equal(1, ExperimentLedgerReader.Read(SampleLedger).OpenPredictions);
    Assert.Contains("1 open prediction(s)", card.Detail, StringComparison.Ordinal);
    Assert.Contains("6 record(s)", card.Detail, StringComparison.Ordinal);
    Assert.True(card.HasOpenPredictions);
    Assert.False(vm.IsEmpty);
    Assert.Null(vm.LoadError);
  }

  /// <summary>Only <c>sci.&lt;slug&gt;/ledger</c> keys are ledgers: a state key that merely
  ///     lives under a <c>sci.</c> namespace is not one, and rendering it would offer a ledger
  ///     the experiment tool would never read.</summary>
  [Fact]
  public void Refresh_ListsOnlyLedgerKeys_NotEveryKeyInTheNamespace()
  {
    Assert.True(ExperimentLedgerKey.IsLedger("sci.my-plan/ledger"));
    Assert.False(ExperimentLedgerKey.IsLedger("sci.my-plan/notes"));
    Assert.False(ExperimentLedgerKey.IsLedger("sdd.my-plan/ledger"));
    Assert.False(ExperimentLedgerKey.IsLedger("ledger"));
  }

  /// <summary>D2's ordering: a ledger still holding open predictions is mid-flight, so it
  ///     sorts above one that is finished.
  ///     </summary>
  [Fact]
  public async Task Refresh_OrdersLedgersWithOpenPredictionsFirst()
  {
    FakeLedgerAccess access = new();
    access.Ledgers["sci.aaa-finished/ledger"] =
        "hypothesis 1: done\nobservation 1: settled";
    access.Ledgers["sci.zzz-open/ledger"] = SampleLedger;
    LedgerViewViewModel vm = Build(access);

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.Equal(["zzz-open", "aaa-finished"], vm.Ledgers.Select(card => card.Slug));
  }

  /// <summary>D5: the stored line renders VERBATIM. A view that reassembled the line from its
  ///     parsed pieces would be a second definition of the ledger's format, and it would break
  ///     the first time a session wrote something slightly different.</summary>
  [Fact]
  public async Task Select_RendersTheStoredLinesVerbatim()
  {
    FakeLedgerAccess access = new();
    access.Ledgers["sci.318-ledger-view/ledger"] = SampleLedger;
    LedgerViewViewModel vm = Build(access);
    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    await vm.SelectAsync(Assert.Single(vm.Ledgers), TestContext.Current.CancellationToken);

    string[] stored = SampleLedger.Split('\n');
    Assert.Equal(stored.Length, vm.Records.Count);
    for (int i = 0; i < stored.Length; i++)
    {
      Assert.Equal(stored[i], vm.Records[i].Text);
    }
  }

  /// <summary>D3: a REFUTED verdict is visually distinct. It is the highest-value record in a
  ///     ledger - it is what stops the next session spending budget on something already
  ///     disproved - so it carries its own marker as well as its own colour, because colour
  ///     alone is not a distinction every reader receives.</summary>
  [Fact]
  public async Task Select_MarksARefutedVerdictDistinctly()
  {
    FakeLedgerAccess access = new();
    access.Ledgers["sci.refutation/ledger"] =
        "hypothesis 1: the cache is warm\n" +
        "prediction 1 (hypothesis 1): a second read is instant \u2014 falsified by: time it\n" +
        "verdict 1 (prediction 1): refuted \u2014 4.2s cold, 4.1s warm";
    LedgerViewViewModel vm = Build(access);
    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    await vm.SelectAsync(Assert.Single(vm.Ledgers), TestContext.Current.CancellationToken);

    LedgerLineViewModel refuted = Assert.Single(vm.Records, line => line.IsRefuted);
    Assert.Contains("refuted", refuted.Text, StringComparison.Ordinal);
    // Its own marker, so the distinction survives a reader who cannot separate the hues.
    Assert.Equal("\u2713", refuted.Marker);
    // No other line claims to be refuted: a marker on everything is a marker on nothing.
    Assert.Equal(1, vm.Records.Count(line => line.IsRefuted));
  }

  /// <summary>A CONFIRMED verdict is not a refutation, so the two never render alike.</summary>
  [Fact]
  public async Task Select_DoesNotMarkAConfirmedVerdictAsRefuted()
  {
    FakeLedgerAccess access = new();
    access.Ledgers["sci.confirmed/ledger"] = SampleLedger;
    LedgerViewViewModel vm = Build(access);
    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    await vm.SelectAsync(Assert.Single(vm.Ledgers), TestContext.Current.CancellationToken);

    Assert.DoesNotContain(vm.Records, line => line.IsRefuted);
    LedgerLineViewModel verdict = Assert.Single(vm.Records,
        line => line.Kind == ExperimentRecordKind.Verdict);
    Assert.Contains("confirmed", verdict.Text, StringComparison.Ordinal);
  }

  /// <summary>D5's other half: a line the parser did not write still RENDERS. A prose ledger
  ///     (the <c>sdd.*</c> keys are prose) must read as what it says, never as a blank pane and
  ///     never as an invented record.</summary>
  [Fact]
  public async Task Select_RendersUnrecognizedLinesRatherThanDroppingThem()
  {
    FakeLedgerAccess access = new();
    access.Ledgers["sci.prose/ledger"] = "this is not a ledger line\nneither is this";
    LedgerViewViewModel vm = Build(access);
    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    await vm.SelectAsync(Assert.Single(vm.Ledgers), TestContext.Current.CancellationToken);

    Assert.Equal(2, vm.Records.Count);
    Assert.All(vm.Records, line => Assert.Equal(ExperimentRecordKind.Unrecognized, line.Kind));
    Assert.Equal("this is not a ledger line", vm.Records[0].Text);
  }

  /// <summary>A storage failure is not 'no ledgers' - the two must never look the same.
  ///     </summary>
  [Fact]
  public async Task Refresh_LoadFailure_RendersAnError_NotAnEmptyState()
  {
    FakeLedgerAccess access = new()
    {
      ListFailure = new DomainError("StorageUnavailable", "the database is locked"),
    };
    LedgerViewViewModel vm = Build(access);

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.NotNull(vm.LoadError);
    Assert.Contains("database is locked", vm.LoadError, StringComparison.Ordinal);
    Assert.Equal("error", vm.StatusLine);
  }

  [Fact]
  public async Task Refresh_NoLedgers_ReportsEmpty()
  {
    LedgerViewViewModel vm = Build(new FakeLedgerAccess());

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.True(vm.IsEmpty);
    Assert.Null(vm.LoadError);
  }

  /// <summary>D4: the view's PRIMARY mechanism is the state event, not a poll. The fact is the
  ///     one the ChildHost forwards over the wire, so a ledger a session appends to in the host
  ///     process reaches this view with no polling interval at all.</summary>
  [Fact]
  public async Task StateChangedEvent_RefreshesWithoutAManualCall()
  {
    FakeLedgerAccess access = new();
    FakeEvents events = new();
    LedgerViewViewModel vm = Build(access, events: events);
    vm.Start();
    await vm.RefreshAsync(TestContext.Current.CancellationToken);
    Assert.True(vm.IsEmpty);

    access.Ledgers["sci.new/ledger"] = SampleLedger;
    events.Publish(new StateChangedEvent(new AgentId(Guid.NewGuid()), DateTimeOffset.UtcNow,
        "sci.new/ledger", "append"));

    for (int i = 0; i < 200 && vm.Ledgers.Count == 0; i++)
    {
      await Task.Delay(10, TestContext.Current.CancellationToken);
    }

    _ = Assert.Single(vm.Ledgers);
  }

  /// <summary>Only a STATE fact refreshes this view: the stream carries every child-run event
  ///     in the family, and re-reading the ledgers on each one would make the poll the primary
  ///     mechanism by accident.</summary>
  [Fact]
  public async Task APlanChangedEvent_DoesNotRefreshTheLedgerView()
  {
    FakeLedgerAccess access = new();
    FakeEvents events = new();
    LedgerViewViewModel vm = Build(access, events: events);
    vm.Start();
    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    access.Ledgers["sci.new/ledger"] = SampleLedger;
    events.Publish(new PlanChangedEvent(new AgentId(Guid.NewGuid()), DateTimeOffset.UtcNow,
        1, "steps", null));
    await Task.Delay(100, TestContext.Current.CancellationToken);

    Assert.Empty(vm.Ledgers);
  }

  /// <summary>The change signal is the BACKSTOP, not the primary mechanism: a write that
  ///     published no fact still reaches the view.</summary>
  [Fact]
  public async Task ChangeSignal_MovingVersion_TriggersARefresh()
  {
    FakeLedgerAccess access = new();
    CountingSignal signal = new();
    LedgerViewViewModel vm = Own(new LedgerViewViewModel(
        access, signal, render => render(), TimeSpan.FromMilliseconds(5)));
    vm.Start();
    await vm.RefreshAsync(TestContext.Current.CancellationToken);
    Assert.Empty(vm.Ledgers);

    access.Ledgers["sci.new/ledger"] = SampleLedger;
    signal.Bump();

    for (int i = 0; i < 200 && vm.Ledgers.Count == 0; i++)
    {
      await Task.Delay(10, TestContext.Current.CancellationToken);
    }

    _ = Assert.Single(vm.Ledgers);
  }

  /// <summary>The open ledger survives a refresh BY KEY: the cards are replaced on every read,
  ///     so a selection held only on the old card would close the detail pane the moment
  ///     anything moved - and an appended record would never appear.</summary>
  [Fact]
  public async Task Refresh_KeepsTheOpenLedgerOpenAndPicksUpAnAppendedRecord()
  {
    FakeLedgerAccess access = new();
    access.Ledgers["sci.318-ledger-view/ledger"] = SampleLedger;
    LedgerViewViewModel vm = Build(access);
    await vm.RefreshAsync(TestContext.Current.CancellationToken);
    await vm.SelectAsync(Assert.Single(vm.Ledgers), TestContext.Current.CancellationToken);
    int before = vm.Records.Count;

    access.Ledgers["sci.318-ledger-view/ledger"] = SampleLedger +
        "\nconclusion 1: the snapshot is the honest mechanism";
    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.True(vm.HasSelection);
    Assert.Equal("318-ledger-view", vm.SelectedSlug);
    Assert.Equal(before + 1, vm.Records.Count);
  }

  /// <summary>A ledger that disappears between reads CLOSES its detail pane rather than
  ///     leaving records of a key that no longer exists on screen.</summary>
  [Fact]
  public async Task Refresh_AClosedLedger_ClosesTheDetailPane()
  {
    FakeLedgerAccess access = new();
    access.Ledgers["sci.gone/ledger"] = SampleLedger;
    LedgerViewViewModel vm = Build(access);
    await vm.RefreshAsync(TestContext.Current.CancellationToken);
    await vm.SelectAsync(Assert.Single(vm.Ledgers), TestContext.Current.CancellationToken);
    Assert.True(vm.HasSelection);

    _ = access.Ledgers.Remove("sci.gone/ledger");
    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.False(vm.HasSelection);
    Assert.Null(vm.SelectedSlug);
    Assert.Empty(vm.Records);
  }

  /// <summary>An empty ledger renders as EMPTY, not as a load failure: a plan whose loop has
  ///     not started yet is the ordinary state of a new workspace.</summary>
  [Fact]
  public async Task Select_AnEmptyLedger_SaysItIsEmptyRatherThanFailing()
  {
    FakeLedgerAccess access = new();
    access.Ledgers["sci.fresh/ledger"] = "";
    LedgerViewViewModel vm = Build(access);
    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    await vm.SelectAsync(Assert.Single(vm.Ledgers), TestContext.Current.CancellationToken);

    Assert.True(vm.HasSelection);
    Assert.True(vm.SelectedIsEmpty);
    Assert.Null(vm.LoadError);
  }

  /// <summary>D1: the view has no writer. The port it reads through has no write method at all,
  ///     so read-only is enforced by the type system rather than by discipline - pinned here
  ///     because 'the view happens not to write' and 'the view cannot write' are different
  ///     guarantees and only the second one survives a later edit.</summary>
  [Fact]
  public void TheReadPort_HasNoWriteSurface()
  {
    string[] writers = [.. typeof(IExperimentLedgerAccess).GetMethods()
        .Where(method => method.Name.StartsWith("Set", StringComparison.Ordinal)
            || method.Name.StartsWith("Append", StringComparison.Ordinal)
            || method.Name.StartsWith("Delete", StringComparison.Ordinal)
            || method.Name.StartsWith("Write", StringComparison.Ordinal))
        .Select(method => method.Name)];
    Assert.Empty(writers);
  }

  /// <summary>A view with no seam renders its unavailable state rather than a blank pane, and
  ///     does not throw when asked to refresh.</summary>
  [Fact]
  public async Task CreateUnavailable_RefreshesWithoutFailing()
  {
    LedgerViewViewModel vm = Own(LedgerViewViewModel.CreateUnavailable());

    await vm.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.Empty(vm.Ledgers);
    Assert.Null(vm.LoadError);
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
