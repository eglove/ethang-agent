using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>How a chain reads at a glance (issue #312 D2). PARKED and RUNNING are separate
///     members because the whole reason the record has a <see cref="ChainStatus.Parked"/>
///     status is that 'waiting for you' and 'working' must not look alike; FAILED is
///     deliberately absent, because a chain has no Failed status - a chain that broke is
///     either Parked (waiting for a human) or Abandoned (dropped).</summary>
internal enum ChainStatusKind
{
  Running,
  Parked,
  Completed,
  Abandoned,
  Corrupt,
}

/// <summary>One step line of a chain card: its marker, what it is, and what it recorded.
///     <para>THE STEP NAMES A PLAN POSITION AND A ROLE, never a title: a chain step
///     REFERENCES the plan (#300 D2) and never copies it, so a view that wanted the plan's
///     wording would have to join two records - and a plan edit would silently rewrite
///     history. What the chain actually knows is what is shown.</para></summary>
internal sealed record ChainStepRowViewModel(
    int Position,
    string Marker,
    string Text,
    string? Outcome,
    string? SessionId,
    bool IsSettled,
    ChainStepStatus Status)
{
  /// <summary>Renders one step. The marker comes from <see cref="ChainStep.EffectiveStatus"/>,
  ///     so a Running step whose claim lapsed reads as available again rather than as work in
  ///     progress nobody is doing - the same reading the advance uses.</summary>
  /// <param name="step">The step to render.</param>
  /// <param name="now">The clock the claim window is measured against.</param>
  public static ChainStepRowViewModel For(ChainStep step, DateTimeOffset now)
  {
    ArgumentNullException.ThrowIfNull(step);
    ChainStepStatus status = step.EffectiveStatus(now);
    return new ChainStepRowViewModel(
        step.Position,
        MarkerFor(status),
        string.Create(CultureInfo.InvariantCulture, $"step {step.Position} \u00B7 {step.Role.Encoded}"),
        Truncate(step.Outcome),
        step.SessionId,
        status is ChainStepStatus.Done or ChainStepStatus.Skipped,
        status);
  }

  /// <summary>The marker set, mirroring the Plans view's: Done a check, Running a triangle,
  ///     Parked a pause bar, Failed a cross, Skipped a dash, Pending a circle.
  ///     <para>PARKED AND FAILED ARE DIFFERENT GLYPHS on purpose (#312 D2): a step waiting for a
  ///     human and a step that broke are different facts, and a view that drew them alike would
  ///     undo the distinction the chain's status machine exists to preserve.</para></summary>
  private static string MarkerFor(ChainStepStatus status) => status switch
  {
    ChainStepStatus.Done => "\u2713",
    ChainStepStatus.Running => "\u25B6",
    ChainStepStatus.Parked => "\u23F8",
    ChainStepStatus.Failed => "\u2717",
    ChainStepStatus.Skipped => "\u2014",
    ChainStepStatus.Pending => "\u25CB",
    _ => "\u25CB",
  };

  /// <summary>An outcome can be a whole review's cited findings, so the line is bounded. The
  ///     full text stays on the record; a card is a summary, and a summary that can be any
  ///     length is not one.</summary>
  private static string? Truncate(string? outcome)
  {
    if (string.IsNullOrWhiteSpace(outcome))
    {
      return null;
    }

    string trimmed = outcome.Trim();
    return trimmed.Length <= OutcomeLimit ? trimmed : trimmed[..OutcomeLimit] + "\u2026";
  }

  /// <summary>How much of an outcome a card line shows.</summary>
  internal const int OutcomeLimit = 240;
}

/// <summary>One chain as the rail renders it (issue #312): status, current step, the steps
///     with their outcomes, and - for a parked chain - WHY it stopped and WHERE.
///     <para>EXPANSION IS STATE, not a rendering detail: a completed chain is listed COLLAPSED
///     (D7) while an active one is open, and the user's own toggle must survive the next
///     refresh. That is why this is an observable class rather than a record the refresh
///     replaces wholesale.</para></summary>
internal sealed partial class ChainCardViewModel : ObservableObject
{
  /// <summary>Renders one readable chain. EVERYTHING the card shows is derived HERE, from the
  ///     record and the clock, rather than passed in already-computed: a caller assembling the
  ///     pieces could assemble them differently, and the two places a parked chain's sentence is
  ///     built would then be two chances to disagree.
  ///     <para>The expansion state is the ONE thing the caller supplies, because it is the user's
  ///     choice and not a fact about the record (D7).</para></summary>
  /// <param name="chain">The chain to render.</param>
  /// <param name="now">The clock the claim window is measured against.</param>
  /// <param name="isExpanded">Whether the card shows its steps.</param>
  public ChainCardViewModel(Chain chain, DateTimeOffset now, bool isExpanded)
  {
    ArgumentNullException.ThrowIfNull(chain);
    ChainId = chain.Id;
    Title = $"#{chain.Id} \u00B7 {chain.Title}";
    string session = chain.SessionId is { Length: > 8 } id ? id[..8] : chain.SessionId ?? "harness";
    Subtitle = string.Create(CultureInfo.InvariantCulture,
        $"plan #{chain.PlanId} v{chain.PlanVersion} \u00B7 {chain.Steps.Count} step(s) \u00B7 started by {session}");
    StatusKind = KindOf(chain.Status);
    StatusDisplay = chain.Status.ToString().ToUpperInvariant();
    CurrentStep = CurrentStepFor(chain, now);
    ParkDetail = ParkDetailFor(chain);
    Steps = [.. chain.Steps.OrderBy(step => step.Position).Select(step => ChainStepRowViewModel.For(step, now))];
    IsExpanded = isExpanded;
    CorruptReason = null;
  }

  /// <summary>Renders a chain row that could not be read (issue #279's containment, applied to
  ///     chains): a named marker instead of a blank card, so one corrupt row never blanks the
  ///     workspace's chain list.</summary>
  /// <param name="row">The unreadable row, as the store reported it.</param>
  public ChainCardViewModel(CorruptChainRow row)
  {
    ArgumentNullException.ThrowIfNull(row);
    ChainId = row.ChainId;
    Title = $"#{row.ChainId}";
    Subtitle = "unreadable chain row";
    StatusKind = ChainStatusKind.Corrupt;
    StatusDisplay = "CORRUPT";
    CurrentStep = "";
    ParkDetail = null;
    Steps = [];
    IsExpanded = false;
    CorruptReason = row.Reason;
  }

  /// <summary>The record's id, so a refresh can preserve this card's expansion state.</summary>
  public int ChainId { get; }

  /// <summary>The chain's label and its id, e.g. '#4 \u00B7 ship the rail view'.</summary>
  public string Title { get; }

  /// <summary>The plan it executes, its step count, and its authoring session.</summary>
  public string Subtitle { get; }

  /// <summary>What the status reads as, for the brush and for the classes the view styles.
  ///     </summary>
  public ChainStatusKind StatusKind { get; }

  /// <summary>The status word, e.g. 'parked'.</summary>
  public string StatusDisplay { get; }

  /// <summary>Where the chain is, in one line: the running step, the step it is waiting at, or
  ///     that it is finished.</summary>
  public string CurrentStep { get; }

  /// <summary>For a PARKED chain, the reason and the step it stopped at, in one sentence.
  ///     <para>THIS IS THE WHOLE POINT OF THE CARD (#312 D2, inherited from #303): a chain that
  ///     parked is waiting for a human, and a surface that said only 'parked' would leave the
  ///     user to open the database to find out why.</para>
  ///     <para>Null for every chain that is not parked, so a running chain never renders a
  ///     stale reason.</para></summary>
  public string? ParkDetail { get; }

  /// <summary>True when this card is a corrupt-row marker rather than a readable chain.</summary>
  public string? CorruptReason { get; }

  public bool IsCorrupt => CorruptReason is not null;

  /// <summary>True when the chain is waiting for a human (D2's distinction).</summary>
  public bool IsParked => StatusKind == ChainStatusKind.Parked;

  /// <summary>True when the chain will do nothing more on its own.</summary>
  public bool IsFinished => StatusKind is ChainStatusKind.Completed or ChainStatusKind.Abandoned;

  /// <summary>Whether the card shows its step lines. Defaults OPEN for an active chain and
  ///     CLOSED for a finished one (D7), then follows the user's toggle.</summary>
  [ObservableProperty]
  public partial bool IsExpanded { get; set; }

  /// <summary>True when this card has any step lines to show.</summary>
  public bool HasSteps => Steps.Count > 0;

  /// <summary>True when the chain has at least one step a session can be opened for.</summary>
  public bool CanOpenSession => Steps.Any(step => step.SessionId is not null);

  /// <summary>The step lines, in position order.</summary>
  public IReadOnlyList<ChainStepRowViewModel> Steps { get; }

  /// <summary>The display kind for a chain status. The set is closed, so the fallback names a
  ///     defect rather than guessing - the <c>ChainService.KindOf</c> precedent.</summary>
  /// <param name="status">The chain's status.</param>
  private static ChainStatusKind KindOf(ChainStatus status) => status switch
  {
    ChainStatus.Running => ChainStatusKind.Running,
    ChainStatus.Parked => ChainStatusKind.Parked,
    ChainStatus.Completed => ChainStatusKind.Completed,
    ChainStatus.Abandoned => ChainStatusKind.Abandoned,
    _ => ChainStatusKind.Corrupt,
  };

  /// <summary>Where the chain is: the step it is waiting at when parked, the step a session
  ///     holds when running, and otherwise how many steps settled.
  ///     <para>THE PARKED STEP IS NAMED FROM THE RECORD (<c>parked_at_position</c>), not
  ///     re-derived by scanning for a Parked step: the two can disagree - a chain parked because
  ///     nothing was available has no Parked step at all - and the record's own answer is the one
  ///     the harness acted on.</para></summary>
  /// <param name="chain">The chain to describe.</param>
  /// <param name="now">The clock the claim window is measured against.</param>
  private static string CurrentStepFor(Chain chain, DateTimeOffset now)
  {
    if (chain.ParkedAtPosition is { } parked)
    {
      return string.Create(CultureInfo.InvariantCulture, $"waiting at step {parked}");
    }

    ChainStep? running = chain.Steps.FirstOrDefault(step =>
        step.EffectiveStatus(now) == ChainStepStatus.Running);
    if (running is not null)
    {
      return string.Create(CultureInfo.InvariantCulture, $"running step {running.Position}");
    }

    int settled = chain.Steps.Count(step => step.EffectiveStatus(now)
        is ChainStepStatus.Done or ChainStepStatus.Skipped);
    return string.Create(CultureInfo.InvariantCulture,
        $"{settled} of {chain.Steps.Count} step(s) settled");
  }

  /// <summary>The park sentence: the reason, and the step it stopped at when one was recorded.
  ///     <para>A REASON THAT NAMES NO STEP IS STILL THE REASON, so it renders alone rather than
  ///     being suppressed: a chain parked because nothing was available has no position, and
  ///     'parked' with no explanation is exactly the state this card exists to prevent.</para>
  ///     </summary>
  /// <param name="chain">The chain to describe.</param>
  private static string? ParkDetailFor(Chain chain)
  {
    if (chain.Status != ChainStatus.Parked)
    {
      return null;
    }

    string reason = string.IsNullOrWhiteSpace(chain.ParkReason)
        ? "no reason recorded"
        : chain.ParkReason.Trim();
    return chain.ParkedAtPosition is { } position
        ? string.Create(CultureInfo.InvariantCulture, $"stopped at step {position}: {reason}")
        : reason;
  }
}

/// <summary>The workbench's Chains view (issue #312): every chain in the workspace with its
///     status, its current step, and each step's outcome.
///     <para>ITS OWN RAIL VIEW, BESIDE PLANS (D5), never inside it: a plan is the design and a
///     chain is its execution - different artifacts with different lifetimes - and a user
///     looking for one should not have to scan past the other.</para>
///     <para>EVENT-DRIVEN, with the Plans view's exact shape (D4): the primary source is
///     <see cref="ChainChangedEvent"/> on the session's agent event stream, which the ChildHost
///     forwards over the wire - so a chain the model advances in the host process reaches this
///     view instantly. <see cref="IChangeSignal"/> (SQLite's data_version) is the BACKSTOP for a
///     write that published no fact, never the primary mechanism.</para>
///     <para>READ-ONLY EXCEPT RESUME (D8). The one action opens a chain's session, which is the
///     same operation the Sessions menu performs: the view never mutates the chain record. A
///     human un-parks a chain by resuming its session and steering it, not by editing the
///     cursor from a pane.</para></summary>
internal sealed partial class ChainsViewViewModel(
    ChainService? chains,
    IChangeSignal? signal,
    Action<Action> marshal,
    TimeSpan? pollInterval = null,
    IAgentEvents? events = null,
    Func<string, Task>? resumeSession = null) : ObservableObject, IDisposable
{
  private readonly ChainService? _chains = chains;
  private readonly IAgentEvents? _events = events;
  private readonly IChangeSignal? _signal = signal;
  private readonly Action<Action> _marshal = marshal ?? throw new ArgumentNullException(nameof(marshal));
  private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
  private readonly Func<string, Task>? _resumeSession = resumeSession;
  private readonly CancellationTokenSource _cts = new();
  private readonly Dictionary<int, bool> _expansion = [];
  private List<ChainCardViewModel> _rendered = [];
  private IDisposable? _eventsSubscription;
  private long _lastVersion = long.MinValue;
  private bool _refreshInFlight;
  private bool _refreshAgain;
  private bool _started;
  private bool _disposed;

  /// <summary>A view with no seams: renders its unavailable state. The default a rail gets
  ///     from a container that wired no chain machinery (a headless stub), so the rail stays a
  ///     working pane rather than a blank one.
  ///     <para>STATED ONCE HERE rather than at each construction site: two callers building an
  ///     inert view would be two chances to build it differently.</para>
  ///     <para>A METHOD, NOT A SHARED INSTANCE: the view is disposed with the rail that owns
  ///     it, so one shared instance would be killed by the first tab that closed and every
  ///     later rail would bind a disposed view.</para></summary>
  public static ChainsViewViewModel CreateUnavailable() =>
      new(chains: null, signal: null, marshal: static render => render());

  /// <summary>The rendered chain cards: parked first (they need a human), then running, then
  ///     finished newest-first.</summary>
  public ObservableCollection<ChainCardViewModel> Chains { get; } = [];

  /// <summary>The header's right-hand status: 'live \u00B7 N running \u00B7 M parked', or the
  ///     load failure. A view that cannot read must SAY so rather than look empty.</summary>
  [ObservableProperty]
  public partial string StatusLine { get; private set; } = "live";

  /// <summary>True when the view has nothing to show and no failure to report.</summary>
  [ObservableProperty]
  public partial bool IsEmpty { get; private set; } = true;

  /// <summary>Non-null when the last refresh failed; renders instead of the empty state.
  ///     A storage failure is not 'no chains' - the two must never look the same.</summary>
  [ObservableProperty]
  public partial string? LoadError { get; private set; }

  /// <summary>True when any chain row could not be read. The corrupt cards are in
  ///     <see cref="Chains"/>; this flag only drives a header warning (the #279 containment,
  ///     applied to chains).</summary>
  [ObservableProperty]
  public partial bool HasCorruptRows { get; private set; }

  /// <summary>How many chains are waiting for a human. The status line's badge reads this
  ///     (D6), which is why it is a property rather than a count derived at render time.
  ///     <para>THE RAIL MAY BE CLOSED, so the count is raised as an EVENT as well: the shell
  ///     binds it to a surface that is always visible, because a chain waiting for a human is
  ///     the one thing in this application that needs attention.</para></summary>
  [ObservableProperty]
  public partial int ParkedCount { get; private set; }

  /// <summary>Whether finished chains are listed. True by default - history is the point of the
  ///     record (D7) - and the toggle is the 'filterable' half of that decision.</summary>
  [ObservableProperty]
  public partial bool ShowFinished { get; set; } = true;

  /// <summary>Non-null when the one action could not be performed, naming why. A silent
  ///     no-op would leave the user clicking a button that does nothing.</summary>
  [ObservableProperty]
  public partial string? ActionMessage { get; private set; }

  /// <summary>Raised whenever <see cref="ParkedCount"/> changes, so a surface that is always
  ///     visible can mirror it without polling this view.</summary>
  public event Action<int>? ParkedCountChanged;

  /// <summary>Starts the two realtime sources. Idempotent: a second call is a no-op.
  ///     <para>Called when the view is SELECTED (the Plans view's rule) and also once when the
  ///     tab attaches, because the parked badge must be right before anyone opens the rail.
  ///     </para></summary>
  public void Start()
  {
    if (_disposed || _started)
    {
      return;
    }

    _started = true;
    _eventsSubscription = _events?.Subscribe(new ChainChangeSubscriber(
        () => _ = RefreshAsync(CancellationToken.None)));
    if (_signal is not null)
    {
      _lastVersion = _signal.Read();
      _ = PollAsync(_cts.Token);
    }
  }

  /// <summary>Re-reads the chain store and re-renders. Concurrent calls collapse, and a change
  ///     landing mid-refresh is remembered rather than dropped - the in-flight read may already
  ///     have passed the write.</summary>
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
        await LoadChainsAsync(ct).ConfigureAwait(false);
      }
      while (_refreshAgain && !_disposed);
    }
    finally
    {
      _refreshInFlight = false;
    }
  }

  /// <summary>Opens the session of <paramref name="card"/>'s step that needs a human: the
  ///     parked step when the chain is parked, otherwise the step that is running.
  ///     <para>THE ONE ACTION (D8), and it is the Sessions menu's own operation: a parked chain
  ///     is unblocked by a human reading and steering the session that stopped, never by a
  ///     pane editing the cursor.</para></summary>
  /// <param name="card">The chain whose session to open.</param>
  /// <param name="ct">Cancellation.</param>
  public async Task OpenSessionAsync(ChainCardViewModel card, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(card);
    ActionMessage = null;
    if (_resumeSession is null)
    {
      ActionMessage = "this session has no way to open another session.";
      return;
    }

    string? sessionId = SessionToOpen(card);
    if (sessionId is null)
    {
      ActionMessage = $"chain #{card.ChainId} has no session to open yet.";
      return;
    }

    await _resumeSession(sessionId).ConfigureAwait(false);
    _ = ct;
  }

  /// <summary>The session a reader most wants: the parked step's, else the running one's, else
  ///     the last step that ran. Null when nothing has been started.
  ///     <para>THE PARKED STEP WINS because a parked chain is the case this surface exists for:
  ///     the step that stopped is the one with the answer.</para></summary>
  private static string? SessionToOpen(ChainCardViewModel card)
    => card.Steps.FirstOrDefault(step => step.Status == ChainStepStatus.Parked && step.SessionId is not null)?.SessionId
        ?? card.Steps.FirstOrDefault(step => step.Status == ChainStepStatus.Running && step.SessionId is not null)?.SessionId
        ?? card.Steps.LastOrDefault(step => step.SessionId is not null)?.SessionId;

  private async Task LoadChainsAsync(CancellationToken ct)
  {
    if (_chains is null)
    {
      return;
    }

    Result<ChainCatalog> listed = await _chains.ListAsync(null, ct).ConfigureAwait(false);
    if (!listed.IsSuccess)
    {
      Apply(() =>
      {
        LoadError = $"chains unavailable: {listed.Error.Message}";
        StatusLine = "error";
      });
      return;
    }

    DateTimeOffset now = DateTimeOffset.UtcNow;
    List<ChainCardViewModel> cards = [.. listed.Value.Chains
        .OrderBy(chain => Order(chain.Status))
        .ThenByDescending(chain => chain.Id)
        .Select(chain => ToCard(chain, now))];
    foreach (CorruptChainRow row in listed.Value.Corrupt)
    {
      cards.Add(new ChainCardViewModel(row));
    }

    int parked = listed.Value.Chains.Count(chain => chain.Status == ChainStatus.Parked);
    int running = listed.Value.Chains.Count(chain => chain.Status == ChainStatus.Running);
    Apply(() =>
    {
      _rendered = cards;
      HasCorruptRows = listed.Value.Corrupt.Count > 0;
      LoadError = null;
      StatusLine = string.Create(CultureInfo.InvariantCulture,
          $"live \u00B7 {running} running \u00B7 {parked} parked");
      SetParkedCount(parked);
      RenderChains();
    });
  }

  /// <summary>Ordering: what needs a human first, then what is working, then history.
  ///     <para>An Abandoned chain sorts with Completed rather than above it: both are finished,
  ///     and the distinction between them is on the card, not in the ordering.</para></summary>
  private static int Order(ChainStatus status) => status switch
  {
    ChainStatus.Parked => 0,
    ChainStatus.Running => 1,
    ChainStatus.Completed => 2,
    ChainStatus.Abandoned => 3,
    _ => 4,
  };

  /// <summary>Renders one chain, honouring the expansion the user last chose for it (D7).
  ///     <para>AN UNREMEMBERED CHAIN DEFAULTS OPEN WHILE ACTIVE AND CLOSED ONCE FINISHED: an
  ///     active chain's steps are what the user opened the view for, while a finished one is
  ///     history whose summary line is the answer.</para></summary>
  /// <param name="chain">The chain to render.</param>
  /// <param name="now">The clock the claim window is measured against.</param>
  private ChainCardViewModel ToCard(Chain chain, DateTimeOffset now)
  {
    bool expanded = _expansion.TryGetValue(chain.Id, out bool remembered)
        ? remembered
        : chain.Status is not (ChainStatus.Completed or ChainStatus.Abandoned);
    return new ChainCardViewModel(chain, now, expanded);
  }

  /// <summary>Re-renders the visible cards from the last read, honouring the finished filter.
  ///     <para>SEPARATE FROM THE READ so the filter toggle is a re-render rather than a second
  ///     store round trip.</para></summary>
  private void RenderChains()
  {
    Chains.Clear();
    foreach (ChainCardViewModel card in _rendered)
    {
      if (ShowFinished || !card.IsFinished)
      {
        Chains.Add(card);
      }
    }

    IsEmpty = Chains.Count == 0;
  }

  /// <summary>Re-renders when the finished filter changes: the cards are already read, so this
  ///     is presentation only.</summary>
  /// <param name="value">Whether finished chains are listed.</param>
  partial void OnShowFinishedChanged(bool value)
  {
    _ = value;
    if (!_disposed)
    {
      RenderChains();
    }
  }

  /// <summary>Records the parked count and raises the change, so an always-visible surface can
  ///     mirror it (D6).
  ///     <para>RAISED ON CHANGE ONLY: a badge that re-rendered on every refresh would flicker
  ///     for no reason, and the count is the only thing the listener consumes.</para></summary>
  private void SetParkedCount(int count)
  {
    if (ParkedCount == count)
    {
      return;
    }

    ParkedCount = count;
    ParkedCountChanged?.Invoke(count);
  }

  /// <summary>Applies a render on the UI thread. Every mutation of the observable collection
  ///     goes through here: refreshes complete on pool threads.</summary>
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

  /// <summary>Subscribes the refresh to chain-change facts. Only
  ///     <see cref="ChainChangedEvent"/> is wake-worthy here: every other event in the family
  ///     is child-run telemetry this view does not render.</summary>
  private sealed class ChainChangeSubscriber(Action onChainChanged) : IAgentEventSubscriber
  {
    public void OnEvent(ChildEvent evt)
    {
      if (evt is ChainChangedEvent)
      {
        onChainChanged();
      }
    }
  }

  /// <summary>Remembers the user's expansion choice for <paramref name="card"/> so the next
  ///     refresh preserves it. Called by the view's toggle.
  ///     <para>RECORDED HERE RATHER THAN IN THE VIEW because the card is REPLACED on every
  ///     refresh: a toggle held only on the old card would snap shut the moment anything moved.
  ///     </para></summary>
  /// <param name="card">The card whose expansion the user changed.</param>
  public void RememberExpansion(ChainCardViewModel card)
  {
    ArgumentNullException.ThrowIfNull(card);
    _expansion[card.ChainId] = card.IsExpanded;
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
    // released with the view that owns it (the Plans view's rule).
    (_signal as IDisposable)?.Dispose();
  }
}
