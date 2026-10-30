using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>One record line of a ledger, as the rail renders it (issue #318).
///     <para>THE TEXT IS THE STORED LINE VERBATIM (D5). <see cref="Text"/> is never
///     reassembled from the parsed pieces: a parser that reformatted the line would be a
///     second definition of the format, and it would break the first time a session wrote
///     something slightly different from the documented shape. What the parse contributes is
///     the KIND (for the marker) and whether the verdict was refuted (for D3) - never the
///     wording.</para></summary>
/// <param name="Kind">The record kind the ledger's own parser read.</param>
/// <param name="Marker">The kind's glyph, so a reader can scan by shape as well as by word.</param>
/// <param name="Text">The stored line, verbatim.</param>
/// <param name="IsRefuted">True for a refuted verdict (D3).</param>
internal sealed record LedgerLineViewModel(ExperimentRecordKind Kind, string Marker, string Text,
    bool IsRefuted)
{
  /// <summary>Renders one stored line. The marker distinguishes the kinds at a glance; an
  ///     UNRECOGNIZED line gets its own glyph rather than a guess, because a prose ledger
  ///     (the <c>sdd.*</c> keys) is a legitimate thing to find and rendering it as an
  ///     observation would be inventing a record the session never wrote.</summary>
  /// <param name="line">The parsed line.</param>
  public static LedgerLineViewModel For(ExperimentLedgerLine line)
  {
    ArgumentNullException.ThrowIfNull(line);
    return new LedgerLineViewModel(line.Kind, MarkerFor(line.Kind), line.Raw, line.IsRefuted);
  }

  /// <summary>The marker set: hypothesis a question, prediction an arrow, verdict a check or a
  ///     cross, observation an eye, conclusion a flag, skip a dash, and an unrecognized line a
  ///     middle dot.
  ///     <para>A REFUTED VERDICT GETS ITS OWN GLYPH rather than sharing the confirmed check: D3
  ///     says a refutation is the highest-value record in the ledger, and a marker that drew it
  ///     the same as a confirmation would bury exactly the line a reader is scanning for.</para>
  ///     </summary>
  /// <param name="kind">The record kind.</param>
  private static string MarkerFor(ExperimentRecordKind kind) => kind switch
  {
    ExperimentRecordKind.Hypothesis => "\u003F",
    ExperimentRecordKind.Prediction => "\u2192",
    ExperimentRecordKind.Verdict => "\u2713",
    ExperimentRecordKind.Observation => "\u25C9",
    ExperimentRecordKind.Conclusion => "\u2691",
    ExperimentRecordKind.Skip => "\u2014",
    ExperimentRecordKind.Unrecognized => "\u00B7",
    _ => "\u00B7",
  };
}

/// <summary>One ledger in the rail's list (issue #318 D2): its slug, its record count, and -
///     the number that matters - how many predictions are still open.
///     <para>THE OPEN-PREDICTION COUNT IS THE POINT OF THE LIST: it is what tells a user
///     whether the loop is being run honestly, and it is the same number the finish gate reads,
///     so the list and the gate can never disagree about whether a ledger is finished.</para>
///     </summary>
/// <param name="Key">The ledger's state key.</param>
/// <param name="Slug">The ledger's slug, which is what the rail shows.</param>
/// <param name="Detail">The counts, in one line.</param>
/// <param name="HasOpenPredictions">Whether anything is still waiting on a verdict.</param>
internal sealed record LedgerCardViewModel(string Key, string Slug, string Detail, bool HasOpenPredictions)
{
  /// <summary>Renders one ledger's summary.
  ///     <para>THE COUNTS READ AS WORDS, not as two bare numbers: '3 open prediction(s) of 12
  ///     record(s)' states what is being counted, where '3 / 12' would leave the reader to
  ///     guess which is which.</para></summary>
  /// <param name="summary">The ledger's read summary.</param>
  public static LedgerCardViewModel For(LedgerSummary summary)
  {
    ArgumentNullException.ThrowIfNull(summary);
    string detail = string.Create(CultureInfo.InvariantCulture,
        $"{summary.OpenPredictions} open prediction(s) of {summary.RecordCount} record(s)");
    return new LedgerCardViewModel(summary.Key, summary.Slug, detail, summary.HasOpenPredictions);
  }
}

/// <summary>The workbench's Scientific-method ledger view (issue #318): the workspace's
///     experiment ledgers, each with its open-prediction count, and the records inside one.
///     <para>ITS OWN RAIL VIEW, BESIDE PLANS AND CHAINS (Q18, and #312's D5 for the same
///     reason): the plan is the design, the chain is its execution, and the ledger is what was
///     LEARNED. Three artifacts with three lifetimes, so a user looking for one does not scan
///     past the others.</para>
///     <para>EVENT-DRIVEN (D4), with the Plans and Chains views' exact shape: the primary
///     source is <see cref="StateChangedEvent"/> on the session's agent event stream, which the
///     ChildHost forwards over the wire - so a ledger a session appends to in the host process
///     reaches this view instantly. <see cref="IChangeSignal"/> (SQLite's data_version) is the
///     BACKSTOP for a write that published no fact, never the primary mechanism.</para>
///     <para>WHY THE EVENT STREAM AND NOT <see cref="StateDomain.IStateChangeBus"/> DIRECTLY,
///     which is what D4 names: the bus is IN-PROCESS and session-scoped, and every production
///     turn runs in the ChildHost process. A view subscribing the app container's bus would
///     listen to a bus nobody publishes on and stay silently stale forever - so the fact is
///     translated onto the stream the host already forwards, exactly as plan and chain changes
///     are. The outcome D4 asks for (refresh on state events, not a timer) is what this
///     delivers; the transport is the one that actually reaches this process.</para>
///     <para>READ-ONLY (D1). There is no action and no writer: the ledger is written by
///     sessions through the experiment tool, and a second writer would be a second source of
///     truth.</para></summary>
internal sealed partial class LedgerViewViewModel(
    IExperimentLedgerAccess? ledgers,
    IChangeSignal? signal,
    Action<Action> marshal,
    TimeSpan? pollInterval = null,
    IAgentEvents? events = null) : ObservableObject, IDisposable
{
  private readonly IExperimentLedgerAccess? _ledgers = ledgers;
  private readonly IAgentEvents? _events = events;
  private readonly IChangeSignal? _signal = signal;
  private readonly Action<Action> _marshal = marshal ?? throw new ArgumentNullException(nameof(marshal));
  private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
  private readonly CancellationTokenSource _cts = new();
  private List<LedgerCardViewModel> _rendered = [];
  private IDisposable? _eventsSubscription;
  private long _lastVersion = long.MinValue;
  private bool _refreshInFlight;
  private bool _refreshAgain;
  private bool _started;
  private bool _disposed;

  /// <summary>A view with no seams: renders its unavailable state. The default a rail gets
  ///     from a container that wired no state machinery (a headless stub), so the rail stays a
  ///     working pane rather than a blank one.
  ///     <para>STATED ONCE HERE rather than at each construction site: two callers building an
  ///     inert view would be two chances to build it differently.</para>
  ///     <para>A METHOD, NOT A SHARED INSTANCE: the view is disposed with the rail that owns
  ///     it, so one shared instance would be killed by the first tab that closed and every
  ///     later rail would bind a disposed view.</para></summary>
  public static LedgerViewViewModel CreateUnavailable() =>
      new(ledgers: null, signal: null, marshal: static render => render());

  /// <summary>The rendered ledgers: those with open predictions first (they are mid-flight),
  ///     then the rest by slug.</summary>
  public ObservableCollection<LedgerCardViewModel> Ledgers { get; } = [];

  /// <summary>The records of the ledger the user selected, in stored order. Empty until one is
  ///     opened, which is what the 'select a ledger' hint renders against.</summary>
  public ObservableCollection<LedgerLineViewModel> Records { get; } = [];

  /// <summary>The header's right-hand status: 'live \u00B7 N ledger(s) \u00B7 M open', or the
  ///     load failure. A view that cannot read must SAY so rather than look empty.</summary>
  [ObservableProperty]
  public partial string StatusLine { get; private set; } = "live";

  /// <summary>True when the workspace holds no ledgers and there is no failure to report.
  ///     </summary>
  [ObservableProperty]
  public partial bool IsEmpty { get; private set; } = true;

  /// <summary>Non-null when the last refresh failed; renders instead of the empty state.
  ///     A storage failure is not 'no ledgers' - the two must never look the same.</summary>
  [ObservableProperty]
  public partial string? LoadError { get; private set; }

  /// <summary>The selected ledger's slug, or null when none is open. The detail pane renders
  ///     against this, so a stale selection after a refresh cannot show the wrong records.
  ///     </summary>
  [ObservableProperty]
  public partial string? SelectedSlug { get; private set; }

  /// <summary>True once a ledger is open, so the detail pane replaces the 'select one' hint.
  ///     </summary>
  public bool HasSelection => SelectedSlug is not null;

  /// <summary>True when the selected ledger holds no records: an empty ledger is a fact about
  ///     the workspace, and rendering it as a blank pane would read as a load failure.
  ///     </summary>
  public bool SelectedIsEmpty => HasSelection && Records.Count == 0;

  /// <summary>Starts the two realtime sources. Idempotent: a second call is a no-op.
  ///     <para>Called when the view is SELECTED (the Plans and Chains views' rule), so a view
  ///     nobody is looking at costs nothing.</para></summary>
  public void Start()
  {
    if (_disposed || _started)
    {
      return;
    }

    _started = true;
    _eventsSubscription = _events?.Subscribe(new StateChangeSubscriber(
        () => _ = RefreshAsync(CancellationToken.None)));
    if (_signal is not null)
    {
      _lastVersion = _signal.Read();
      _ = PollAsync(_cts.Token);
    }
  }

  /// <summary>Re-reads the ledgers and re-renders, keeping the open ledger open.
  ///     Concurrent calls collapse, and a change landing mid-refresh is remembered rather than
  ///     dropped - the in-flight read may already have passed the write.</summary>
  /// <param name="ct">Cancellation.</param>
  public async Task RefreshAsync(CancellationToken ct)
  {
    if (_disposed)
    {
      return;
    }

    if (_refreshInFlight)
    {
      _refreshAgain = true;
      return;
    }

    _refreshInFlight = true;
    try
    {
      do
      {
        _refreshAgain = false;
        await LoadLedgersAsync(ct).ConfigureAwait(false);
      }
      while (_refreshAgain && !_disposed);
    }
    finally
    {
      _refreshInFlight = false;
    }
  }

  /// <summary>Opens one ledger's records. A refresh re-reads whatever was open, so a ledger
  ///     gaining an appended record updates in place instead of needing a second click.
  ///     <para>THE SELECTION SURVIVES BY KEY, not by object identity: the cards are replaced on
  ///     every read, so a selection held only on the old card would close the detail pane the
  ///     moment anything moved.</para></summary>
  /// <param name="card">The ledger to open.</param>
  /// <param name="ct">Cancellation.</param>
  public async Task SelectAsync(LedgerCardViewModel card, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(card);
    await LoadRecordsAsync(card.Key, ct).ConfigureAwait(false);
  }

  private async Task LoadLedgersAsync(CancellationToken ct)
  {
    if (_ledgers is null)
    {
      return;
    }

    Result<IReadOnlyList<LedgerSummary>> listed = await _ledgers.ListAsync(ct).ConfigureAwait(false);
    if (!listed.IsSuccess)
    {
      Apply(() =>
      {
        LoadError = $"ledgers unavailable: {listed.Error.Message}";
        StatusLine = "error";
      });
      return;
    }

    // Open predictions first: a ledger mid-flight is the one a user opens the view to watch,
    // and D2's count exists to make that visible without opening anything.
    List<LedgerCardViewModel> cards = [.. listed.Value
        .OrderByDescending(summary => summary.HasOpenPredictions)
        .ThenBy(summary => summary.Slug, StringComparer.Ordinal)
        .Select(LedgerCardViewModel.For)];
    int open = listed.Value.Count(summary => summary.HasOpenPredictions);
    Apply(() =>
    {
      _rendered = cards;
      LoadError = null;
      StatusLine = string.Create(CultureInfo.InvariantCulture,
          $"live \u00B7 {listed.Value.Count} ledger(s) \u00B7 {open} open");
      RenderLedgers();
    });

    // The open ledger is re-read from the SAME refresh that moved it, so an appended record
    // appears in the detail pane without waiting for the next event.
    if (SelectedSlug is { } slug)
    {
      LedgerCardViewModel? still = cards.FirstOrDefault(card =>
          string.Equals(card.Slug, slug, StringComparison.Ordinal));
      if (still is null)
      {
        // The ledger is gone: close the detail pane rather than leave records of a key that
        // no longer exists on screen.
        Apply(ClearSelection);
      }
      else
      {
        await LoadRecordsAsync(still.Key, ct).ConfigureAwait(false);
      }
    }
  }

  private async Task LoadRecordsAsync(string key, CancellationToken ct)
  {
    if (_ledgers is null)
    {
      return;
    }

    Result<ExperimentLedgerSnapshot> read = await _ledgers.ReadAsync(key, ct).ConfigureAwait(false);
    if (!read.IsSuccess)
    {
      Apply(() =>
      {
        LoadError = $"ledger unavailable: {read.Error.Message}";
        StatusLine = "error";
      });
      return;
    }

    List<LedgerLineViewModel> lines = [.. read.Value.Lines.Select(LedgerLineViewModel.For)];
    Apply(() =>
    {
      SelectedSlug = ExperimentLedgerKey.SlugOf(key);
      Records.Clear();
      foreach (LedgerLineViewModel line in lines)
      {
        Records.Add(line);
      }

      LoadError = null;
      OnPropertyChanged(nameof(HasSelection));
      OnPropertyChanged(nameof(SelectedIsEmpty));
    });
  }

  /// <summary>Re-renders the visible list from the last read.</summary>
  private void RenderLedgers()
  {
    Ledgers.Clear();
    foreach (LedgerCardViewModel card in _rendered)
    {
      Ledgers.Add(card);
    }

    IsEmpty = Ledgers.Count == 0;
  }

  /// <summary>Closes the detail pane. Used when the open ledger disappears between reads.
  ///     </summary>
  private void ClearSelection()
  {
    SelectedSlug = null;
    Records.Clear();
    OnPropertyChanged(nameof(HasSelection));
    OnPropertyChanged(nameof(SelectedIsEmpty));
  }

  /// <summary>Applies a render on the UI thread. Every mutation of the observable collections
  ///     goes through here: refreshes complete on pool threads.</summary>
  /// <param name="render">The render to apply.</param>
  private void Apply(Action render)
  {
    if (_disposed)
    {
      return;
    }

    _marshal(render);
  }

  private async Task PollAsync(CancellationToken ct)
  {
    if (_signal is null)
    {
      return;
    }

    using PeriodicTimer timer = new(_pollInterval);
    try
    {
      while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
      {
        long version = _signal.Read();
        if (version == _lastVersion)
        {
          continue;
        }

        _lastVersion = version;
        await RefreshAsync(ct).ConfigureAwait(false);
      }
    }
    catch (OperationCanceledException)
    {
      // Disposal is the only cancellation source.
    }
  }

  /// <summary>Subscribes the refresh to state-change facts. Only <see cref="StateChangedEvent"/>
  ///     is wake-worthy here: every other event in the family is child-run telemetry this view
  ///     does not render.</summary>
  private sealed class StateChangeSubscriber(Action onStateChanged) : IAgentEventSubscriber
  {
    public void OnEvent(ChildEvent evt)
    {
      if (evt is StateChangedEvent)
      {
        onStateChanged();
      }
    }
  }

  public void Dispose()
  {
    if (_disposed)
    {
      return;
    }

    _disposed = true;
    _cts.Cancel();
    _cts.Dispose();
    _eventsSubscription?.Dispose();
    // The change signal holds a live database connection for its whole lifetime, so it is
    // released with the view that owns it (the Plans and Chains views' rule).
    (_signal as IDisposable)?.Dispose();
  }
}
