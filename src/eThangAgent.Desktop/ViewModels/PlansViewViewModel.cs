using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using eThangAgent.AgentDomain;
using eThangAgent.PlanDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>One plan as the workbench renders it: the envelope line, the step markers,
///     and the corrupt-row marker when the plan could not be read (issue #279).</summary>
internal sealed record PlanCardViewModel(
    int PlanId,
    string Title,
    string Subtitle,
    IReadOnlyList<PlanStepRowViewModel> Steps,
    bool IsActive,
    string? CorruptReason)
{
  /// <summary>True when this card is a corrupt-row marker rather than a readable plan.</summary>
  public bool IsCorrupt => CorruptReason is not null;
}

/// <summary>One step line: the marker glyph, the text, and whether it is done.</summary>
internal sealed record PlanStepRowViewModel(int Position, string Marker, string Text, bool IsDone)
{
  /// <summary>Renders a step status as the design's marker set: Pending circle,
  ///     InProgress triangle, Done check (struck through and dimmed by the view), and a
  ///     strike-through dash for Skipped (issue #271). Skipped is deliberately NOT given
  ///     the Pending circle: 'deliberately not doing this' and 'not started yet' are
  ///     different facts, and a view that drew them alike would undo the distinction the
  ///     completion invariant depends on.</summary>
  public static PlanStepRowViewModel For(PlanStep step)
  {
    ArgumentNullException.ThrowIfNull(step);
    string marker = step.Status switch
    {
      PlanStepStatus.Done => "\u2713",
      PlanStepStatus.InProgress => "\u25B6",
      PlanStepStatus.Skipped => "\u2014",
      PlanStepStatus.Pending => "\u25CB",
      _ => "\u25CB",
    };
    return new PlanStepRowViewModel(step.Position, marker, step.Title,
        step.Status is PlanStepStatus.Done or PlanStepStatus.Skipped);
  }
}

/// <summary>The workbench's Plans view (issue #8): this session's plan records, refreshing
///     as the agent changes them.
///     <para>THREE sources, because the writer can be in either process. In-process writes
///     raise <see cref="PlanService"/> events; a plan the agent writes is written by the
///     ChildHost, so the view also
///     subscribes the session's agent event stream for <see cref="PlanChangedEvent"/> —
///     the INSTANT path, because the host already forwards those facts over the wire —
///     and keeps an <see cref="IChangeSignal"/> (SQLite's data_version) as a backstop that
///     re-reads when another process commits. Every path funnels into one
///     <see cref="RefreshAsync"/>, so there is exactly one renderer and no chance of two
///     disagreeing.</para>
///     <para>The event stream is the primary mechanism and the poll is the safety net:
///     the same <see cref="PlanChangedEvent"/> a parent agent watches with
///     <c>wakeOn=plan</c> is what refreshes this view, so a subagent marking a step done
///     is visible here without a polling interval.</para>
///     <para>Read-only by design (D4): the view never mutates a plan. A corrupt plan row
///     renders as a named marker instead of blanking the list (#279).</para>
///     <para>The todo list this view used to render beside the plans is gone (issue #310):
///     the todo tool was retired in favour of plan steps, chain records, and the scientific
///     ledger, so there is no second collection to read and no state-bus branch to
///     subscribe. #312 puts the chain view BESIDE this view, so this one got smaller
///     rather than broken.</para></summary>
internal sealed partial class PlansViewViewModel(
    PlanService? plans,
    IChangeSignal? signal,
    Action<Action> marshal,
    TimeSpan? pollInterval = null,
    IAgentEvents? events = null) : ObservableObject, IDisposable
{
  private readonly PlanService? _plans = plans;
  private readonly IAgentEvents? _events = events;
  private readonly IChangeSignal? _signal = signal;
  private readonly Action<Action> _marshal = marshal ?? throw new ArgumentNullException(nameof(marshal));
  private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
  private readonly CancellationTokenSource _cts = new();
  private PlanEventSubscription? _planSubscription;
  private long _lastVersion = long.MinValue;
  private bool _refreshInFlight;

  /// <summary>Set when a refresh was requested while one was already running. Without it
  ///     a change landing mid-refresh would be dropped: the in-flight read may have
  ///     already passed the write, and the poller would have consumed the version.</summary>
  private bool _refreshAgain;
  private bool _disposed;

  /// <summary>The rendered plan cards, newest first. Empty renders the empty state.</summary>
  public ObservableCollection<PlanCardViewModel> Plans { get; } = [];

  /// <summary>The header's right-hand status: 'live · N active', or the load failure.
  ///     A view that cannot read must SAY so rather than look empty.</summary>
  [ObservableProperty]
  public partial string StatusLine { get; private set; } = "live";

  /// <summary>True when the view has nothing to show and no failure to report.</summary>
  [ObservableProperty]
  public partial bool IsEmpty { get; private set; } = true;

  /// <summary>Non-null when the last refresh failed; renders instead of the empty state.
  ///     A storage failure is not 'no plans' - the two must never look the same.</summary>
  [ObservableProperty]
  public partial string? LoadError { get; private set; }

  /// <summary>True when any plan row could not be read (issue #279). The corrupt cards
  ///     are in <see cref="Plans"/>; this flag only drives a header warning.
  ///     <para>Declared after <see cref="Plans"/> so its change notification lands with
  ///     the collection already updated.</para></summary>
  [ObservableProperty]
  public partial bool HasCorruptRows { get; private set; }

  /// <summary>Starts the two realtime sources. Idempotent: a second call is a no-op.
  ///     Called by the view when it becomes visible, so a collapsed workbench costs
  ///     nothing - there is no reason to poll for a pane nobody is looking at.</summary>
  public void Start()
  {
    if (_disposed || _planSubscription is not null)
    {
      return;
    }

    // Source A: in-process plan events.
    if (_plans is not null)
    {
      _planSubscription = new PlanEventSubscription(_plans,
          () => _ = RefreshAsync(CancellationToken.None));
    }

    // Source B: the session's agent event stream (the INSTANT path). A plan written by
    // the model is written by the ChildHost, so this process's PlanService events can
    // never fire for it - the host forwards a PlanChangedEvent over the wire instead,
    // and the runtime republishes it onto this stream. This is the same fact a parent
    // agent wakes on with wakeOn=plan, so one subscribable serves both readers.
    _eventsSubscription = _events?.Subscribe(new PlanChangeSubscriber(
        () => _ = RefreshAsync(CancellationToken.None)));

    // Source C: cross-process commits, discovered by polling data_version. A BACKSTOP,
    // not the primary mechanism: the event stream above delivers a plan change instantly,
    // while this catches anything that did not publish a fact (a direct SQL write, a
    // future writer that forgets the event). The baseline is seeded HERE, not on the
    // first tick: a change landing between Start and that tick would otherwise be adopted
    // as the baseline and never noticed.
    if (_signal is not null)
    {
      _lastVersion = _signal.Read();
      _ = PollAsync(_cts.Token);
    }
  }

  /// <summary>The agent event stream lease (the instant path). Disposed with the view, so
  ///     a closed tab cannot keep re-reading the database from a dead view.</summary>
  private IDisposable? _eventsSubscription;

  /// <summary>Subscribes the refresh to plan-change facts. Only <see cref="PlanChangedEvent"/>
  ///     is wake-worthy here: every other event in the family is child-run telemetry this
  ///     view does not render.</summary>
  private sealed class PlanChangeSubscriber(Action onPlanChanged) : IAgentEventSubscriber
  {
    public void OnEvent(ChildEvent evt)
    {
      if (evt is PlanChangedEvent)
      {
        onPlanChanged();
      }
    }
  }

  /// <summary>Re-reads the plan store and re-renders. Concurrent calls collapse: a refresh
  ///     already in flight means the next one would read the same state, and the poller
  ///     will catch anything newer.</summary>
  public async Task RefreshAsync(CancellationToken ct)
  {
    if (_disposed)
    {
      return;
    }

    if (_refreshInFlight)
    {
      // Collapse into the running refresh, but remember that another read is owed: the
      // in-flight one may have already passed the write that prompted this call.
      _refreshAgain = true;
      return;
    }

    _refreshInFlight = true;
    try
    {
      do
      {
        _refreshAgain = false;
        await LoadPlansAsync(ct).ConfigureAwait(false);
      }
      while (_refreshAgain && !_disposed);
    }
    finally
    {
      _refreshInFlight = false;
    }
  }

  private async Task LoadPlansAsync(CancellationToken ct)
  {
    if (_plans is null)
    {
      return;
    }

    Result<PlanCatalog> listed = await _plans.ListAsync(null, ct).ConfigureAwait(false);
    if (!listed.IsSuccess)
    {
      Apply(() =>
      {
        LoadError = $"plans unavailable: {listed.Error.Message}";
        StatusLine = "error";
      });
      return;
    }

    // Active plans first (the question 'what is this session doing'), then newest id first
    // inside each group - the design's ordering.
    List<PlanCardViewModel> cards = [.. listed.Value.Plans
        .OrderByDescending(p => p.Status == PlanStatus.Active)
        .ThenByDescending(p => p.Id)
        .Select(ToCard)];
    foreach (CorruptPlanRow row in listed.Value.Corrupt)
    {
      cards.Add(new PlanCardViewModel(row.PlanId, $"#{row.PlanId}", "unreadable plan row",
          [], IsActive: false, row.Reason));
    }

    int active = listed.Value.Plans.Count(p => p.Status == PlanStatus.Active);
    Apply(() =>
    {
      Plans.Clear();
      foreach (PlanCardViewModel card in cards)
      {
        Plans.Add(card);
      }

      HasCorruptRows = listed.Value.Corrupt.Count > 0;
      LoadError = null;
      StatusLine = $"live \u00B7 {active.ToString(CultureInfo.InvariantCulture)} active";
      UpdateEmpty();
    });
  }

  private void UpdateEmpty() => IsEmpty = Plans.Count == 0;

  private static PlanCardViewModel ToCard(Plan plan)
  {
    string session = plan.SessionId.Length > 8 ? plan.SessionId[..8] : plan.SessionId;
    string subtitle = string.Create(CultureInfo.InvariantCulture,
        $"{plan.Status} \u00B7 {plan.Steps.Count} step(s) \u00B7 session {session}");
    return new PlanCardViewModel(
        plan.Id,
        $"#{plan.Id} \u00B7 {plan.Title}",
        subtitle,
        [.. plan.Steps.Select(PlanStepRowViewModel.For)],
        plan.Status == PlanStatus.Active,
        CorruptReason: null);
  }

  /// <summary>Applies a render on the UI thread. Every mutation of the observable
  ///     collections goes through here: refreshes complete on pool threads.
  ///     <para>Declared last in the file so no observable property is assigned before
  ///     the collection it describes.</para></summary>
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

  public void Dispose()
  {
    if (_disposed)
    {
      return;
    }

    _disposed = true;
    _cts.Cancel();
    _cts.Dispose();
    _planSubscription?.Dispose();
    _eventsSubscription?.Dispose();
    // The change signal holds a live database connection for its whole lifetime (that
    // persistence is what makes data_version meaningful), so it must be released with
    // the view that owns it.
    (_signal as IDisposable)?.Dispose();
  }

  /// <summary>Subscribes to all three PlanService events and unsubscribes them together.
  ///     A single IDisposable keeps the disposal path one call, so a tab close cannot
  ///     leave a live subscription rendering into a dead view.</summary>
  private sealed class PlanEventSubscription : IDisposable
  {
    private readonly PlanService _service;
    private readonly Action _onChange;

    public PlanEventSubscription(PlanService service, Action onChange)
    {
      _service = service;
      _onChange = onChange;
      _service.Created += HandleCreated;
      _service.StepChanged += HandleStepChanged;
      _service.StatusChanged += HandleStatusChanged;
    }

    private void HandleCreated(PlanCreated e) => _onChange();

    private void HandleStepChanged(PlanStepChanged e) => _onChange();

    private void HandleStatusChanged(PlanStatusChanged e) => _onChange();

    public void Dispose()
    {
      _service.Created -= HandleCreated;
      _service.StepChanged -= HandleStepChanged;
      _service.StatusChanged -= HandleStatusChanged;
    }
  }
}
