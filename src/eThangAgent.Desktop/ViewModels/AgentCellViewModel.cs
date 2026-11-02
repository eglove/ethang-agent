using CommunityToolkit.Mvvm.ComponentModel;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>One agent cell inside a workspace tab (issue #307 D1): an agent and its chat
///     surface, laid out beside the others.
///     <para>There is no orchestrator agent and therefore no privileged cell (D4/D9): every
///     cell owns its own chat input, so the user can steer any running agent or answer a
///     question from any waiting one.</para>
///     <para>TWO KINDS OF AGENT OCCUPY A CELL (issue #329), and the distinction is WHO OWNS
///     THE SESSION'S CONTAINER. A CONTAINER cell is a session this app holds: it owns the
///     <see cref="AgentSession"/>, the app drives its turns, and the cell's transcript is
///     filled by that session's own stream. A HOST cell is an agent running in its own host
///     process: nothing here owns it, so <see cref="Container"/> is null and the cell follows
///     it through the shared store and the event stream. Both occupy a grid position and
///     render through the same template, which is why they are one type.</para>
///     <para>A cell whose agent is done PERSISTS as a finished card until the user dismisses
///     it (D7). It never reflows: in a grid the position carries meaning.</para></summary>
internal sealed partial class AgentCellViewModel : ObservableObject
{
  /// <summary>How often a running host agent's transcript is re-read from the shared store
  ///     (parent #325 D4). The agent runs in another process and persists at its own safe
  ///     points, so the store is the only seam that sees its messages.</summary>
  internal static readonly TimeSpan TranscriptPollInterval = TimeSpan.FromMilliseconds(500);

  /// <param name="session">The session this cell owns.</param>
  /// <param name="sessionVm">The view-model rendering that session's transcript.</param>
  /// <param name="placement">The chain step this agent executes, or null when it is not one.</param>
  /// <param name="label">The caption this cell shows (issue #338), or null for the workspace
  ///     directory name a container cell otherwise takes. Only a model's launch names one: a
  ///     caller that started several agents must be able to say which one it meant.</param>
  public AgentCellViewModel(AgentSession session, AgentSessionViewModel sessionVm,
      ChainPlacement? placement = null, string? label = null)
  {
    Container = session ?? throw new ArgumentNullException(nameof(session));
    ViewModel = sessionVm ?? throw new ArgumentNullException(nameof(sessionVm));
    RootId = session.RootId;
    Placement = placement;
    Label = label;
    // The outline follows the turn (D5): a stalled or waiting agent must be visible without
    // reading its transcript, so the cell watches the session's busy flag.
    ViewModel.PropertyChanged += OnSessionPropertyChanged;
  }

  /// <summary>Builds a cell over an agent this app does NOT own (issue #329): the agent runs
  ///     in its own host process, so there is no container here to hold - the cell follows it
  ///     through the shared store and the event stream instead.
  ///     <para>Nothing polls until <see cref="StartFollowing"/> is called, so a cell built
  ///     for a settled agent, or by a test, performs no reads at all.</para></summary>
  /// <param name="agentId">The agent's id, as the started fact named it.</param>
  /// <param name="label">The label the agent's record carries (D9: the UI shows a label,
  ///     the database keeps the id).</param>
  /// <param name="store">The shared agent store, which is where the agent's transcript lands.</param>
  /// <param name="sessionVm">The view-model rendering the agent's transcript.</param>
  /// <param name="placement">The chain step this agent executes, or null when it was started
  ///     by hand (issue #329).</param>
  public AgentCellViewModel(AgentId agentId, string label, IAgentStore? store,
      AgentSessionViewModel sessionVm, ChainPlacement? placement = null)
  {
    ArgumentNullException.ThrowIfNull(sessionVm);
    RootId = agentId;
    Label = label;
    ViewModel = sessionVm;
    _store = store;
    Placement = placement;
    RunsInOwnHost = true;
    ViewModel.PropertyChanged += OnSessionPropertyChanged;
  }

  /// <summary>The session container this cell owns, or null when it owns none: an agent running
  ///     in its own host process (issue #329), or a session cell whose session has been retired.
  ///     <para>Every path that disposes a container or completes a session must check
  ///     <see cref="RunsInOwnHost"/> first - completing such an agent's row here would tell
  ///     the store that a live run had finished.</para>
  ///     <para>NULLED BY <see cref="RetireContainer"/>, never left naming a disposed provider: the
  ///     tab's own <c>Container</c> resolves through the cells, and a disposed service provider
  ///     answering that lookup is how a finished card would break the tab around it.</para></summary>
  public AgentSession? Container { get; private set; }

  /// <summary>The agent this cell shows. THIS IS THE CELL'S IDENTITY: the grid never keys a
  ///     cell by position (D7 - the position is the order agents started), so every lookup
  ///     goes through this id.</summary>
  public AgentId RootId { get; }

  /// <summary>The label the agent's record carries, or the caption a launch named (issue
  ///     #338). Null for a cell that named none, whose caption is then its workspace directory
  ///     name.</summary>
  public string? Label { get; }

  /// <summary>True when this cell shows an agent running in its OWN host process rather than a
  ///     session this app owns (issue #329). It is the one kind distinction that remains: the
  ///     app owns no container, no turn loop, and no inbox for that agent.</summary>
  public bool RunsInOwnHost { get; }

  /// <summary>The chain step this agent executes, or null when the user started it by hand
  ///     (issue #329). Read from the chain record's own <c>session_id</c> link, once, at
  ///     creation.</summary>
  public ChainPlacement? Placement { get; }

  /// <summary>The chain placement line the header renders, or null when this agent is not part
  ///     of any chain - the 'nothing extra' half of the acceptance criteria.</summary>
  public string? PlacementLine => Placement?.Line;

  public AgentSessionViewModel ViewModel { get; }

  /// <summary>True once this cell's agent has closed or settled. The cell stays in the grid
  ///     as a finished card (D7) - it is not removed and the remaining cells do not
  ///     reflow.</summary>
  [ObservableProperty]
  public partial bool IsFinished { get; private set; }

  /// <summary>The live phase chip beside the label (issue #331), from the closed vocabulary in
  ///     <see cref="CellPhaseChip"/>. A cell that exists and has heard no beat yet reads
  ///     <c>Starting</c>.
  ///     <para>RE-RAISED ON EVERY BEAT: a progress fact that lands on a cell already reading
  ///     <c>Thinking</c> must still notify, because a binding that only woke on a CHANGE would
  ///     leave the chip looking stuck for the whole of a long tool run.</para></summary>
  [ObservableProperty]
  public partial string PhaseChip { get; private set; } = CellPhaseChip.Starting;

  /// <summary>The agent's final report (parent #325 D5), read from the shared store once it
  ///     settles. Null while the agent runs, and null when the record carries no report.
  ///     <para>READ FROM THE STORE, NEVER FROM THE FACT: <see cref="AgentSettledEvent"/> carries
  ///     a byte count as a size hint, so the report has no other source.</para></summary>
  [ObservableProperty]
  public partial string? Report { get; private set; }

  /// <summary>Whether the card renders a report panel at all.</summary>
  public bool HasReport => !string.IsNullOrWhiteSpace(Report);

  /// <summary>Re-raises <see cref="HasReport" /> when the report lands: it is a computed
  ///     property, so the generated setter's own notification says nothing about it and the
  ///     panel would stay hidden on a card that has a report.</summary>
  /// <param name="value">The report just assigned.</param>
  partial void OnReportChanged(string? value) => OnPropertyChanged(nameof(HasReport));

  /// <summary>Why the card shows no report, when the read that should have produced one
  ///     failed (issue #331). Null when the read succeeded or has not run.
  ///     <para>A FAILED READ IS STATED, NOT LEFT BLANK: a finished card with an empty panel
  ///     reads as 'this agent concluded nothing', which is a different fact from 'the report
  ///     could not be read'.</para></summary>
  [ObservableProperty]
  public partial string? ReportNotice { get; private set; }

  /// <summary>Whether the card carries the alert outline (parent #325 D12): an idle or budget
  ///     alert raised for this agent's run.</summary>
  [ObservableProperty]
  public partial bool IsAlerting { get; private set; }

  /// <summary>Whether this cell's agent is currently streaming a turn. Every cell renders
  ///     LIVE (D3), so this drives the outline rather than whether the cell renders at all
  ///     (D5).
  ///     <para>An agent in its own host is streaming while it has not settled: this app never
  ///     sees its turn boundaries, only that it started and that it finished.</para></summary>
  public bool IsStreaming => RunsInOwnHost ? !IsFinished : ViewModel.IsBusy;

  /// <summary>Whether the cell renders the distinct idle outline (D5). A finished card is
  ///     idle by definition, and a busy agent never is.</summary>
  public bool IsIdle => !IsFinished && !IsStreaming;

  /// <summary>The cell's caption. A chain step shows its chain's title, because the chain is
  ///     the piece of work and the position line beneath it says which step this is
  ///     (issue #329).
  ///     <para>Any other agent shows its label - the label the spawn named, or the workspace
  ///     directory name a container cell has - falling back to the short id when there is no
  ///     name at all (a drive root such as C:\, or an agent that named no label).</para></summary>
  public string Title
  {
    get
    {
      if (Placement is { } placement && !string.IsNullOrWhiteSpace(placement.ChainTitle))
      {
        return placement.ChainTitle;
      }

      if (RunsInOwnHost)
      {
        return string.IsNullOrWhiteSpace(Label) ? RootId.ToString()[..8] : Label;
      }

      // A LAUNCHED CELL SHOWS THE NAME IT WAS GIVEN (issue #338). Several agents in one
      // workspace would otherwise all be captioned with that workspace's directory name, which
      // makes the grid unable to say which agent is which.
      if (!string.IsNullOrWhiteSpace(Label))
      {
        return Label;
      }

      string root = Container?.WorkspaceRoot ?? ViewModel.WorkspaceRoot;
      string name = Path.GetFileName(
          root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
      return string.IsNullOrEmpty(name) ? RootId.ToString()[..8] : name;
    }
  }

  /// <summary>Releases this cell's claim on its session container (issue #329): the shell has
  ///     torn the session down, so the cell must stop naming it. The card stays - the cell keeps
  ///     its position and its finished outline, and only the disposed provider goes.
  ///     <para>THE CHAT INPUT GOES WITH IT (issue #330): there is no session behind a retired
  ///     container to serve a turn, so the card must not offer an input that would do nothing.
  ///     A HOST cell keeps its input - its agent lives in another process and can still be
  ///     steered or resumed - which is why this is told to the view-model rather than inferred
  ///     from <see cref="Container"/> being null.</para></summary>
  public void RetireContainer()
  {
    Container = null;
    if (!RunsInOwnHost)
    {
      ViewModel.MarkRetired();
    }
  }

  /// <summary>Marks the cell finished. The session is already gone (or going); nothing here
  ///     disposes it, because the shell owns that teardown and must run it exactly once.
  ///     Calling again is a no-op, so a close path and a shutdown path cannot disagree.
  ///     <para>A host cell stops following its agent here: the one final read (D4) has
  ///     already been started, and a settled agent's store rows do not change again.
  ///     THE POLLER IS RETIRED, not merely paused (issue #330 review F1): its claim is
  ///     released here, so a later RESUME can start a new loop. Leaving the claim held is
  ///     what made a resumed agent's card look live while nothing read its transcript again.
  ///     </para></summary>
  public void MarkFinished()
  {
    if (IsFinished)
    {
      return;
    }

    IsFinished = true;
    if (RunsInOwnHost)
    {
      // Retire the poller BEFORE the final read: releasing the generation stops the loop at its
      // next tick, and the read is chained behind whatever that loop has in flight, so the
      // card's last content lands even though the loop is done.
      // The terminal row and the report land AFTER the settled event, so the final read is
      // what puts them in the card.
      PhaseChip = CellPhaseChip.Settling;
      StopFollowing();
      _ = FinishFollowingAsync();
    }
    else
    {
      // A container cell's report belongs to the session that owns it, not to the shared store,
      // so there is nothing to read: the run is over the moment the session closes.
      PhaseChip = CellPhaseChip.Finished;
    }

    // The view-model is what the submit path asks (issue #330): a settled agent is RESUMED, a
    // live one is STEERED, and only the cell knows which it is.
    ViewModel.MarkSettled(true);
    RaiseStateChanged();
  }

  /// <summary>Detaches the session subscription and stops the host agent's poller. Called
  ///     when the tab drops the cell, so a dismissed card cannot keep raising property
  ///     changes for a cell nothing renders.</summary>
  public void Detach()
  {
    ViewModel.PropertyChanged -= OnSessionPropertyChanged;
    StopFollowing();
  }

  /// <summary>Stops the host agent's poller. Called from <see cref="Detach"/>, from
  ///     <see cref="MarkFinished"/>, and from the tab's teardown, so a dismissed or finished
  ///     card cannot keep reading the store.
  ///     <para>A GENERATION, NOT A CANCELLATION SOURCE: the loop holds no unmanaged state and
  ///     its next tick is at most one interval away, so a token would buy a latency the cell
  ///     does not need while making the cell itself disposable - and therefore obliging every
  ///     caller to own its lifetime. The loop re-reads its generation on every pass.</para>
  ///     <para>RELEASING THE CLAIM IS HALF THE POINT (issue #330 review F1): this retires the
  ///     live loop AND frees the slot, so a later <see cref="StartFollowing"/> starts a poller
  ///     instead of finding the cell still marked as following. A plain flag did the first and
  ///     not the second, which is why a resumed agent's cell never followed its new run.</para>
  ///     </summary>
  public void StopFollowing()
  {
    lock (_followGate)
    {
      _followingGeneration = 0;
    }
  }

  /// <summary>Starts following an agent running in its own host (issue #329): reads its
  ///     transcript now, then every <see cref="TranscriptPollInterval"/> until the cell is
  ///     finished (parent #325 D4).
  ///     <para>IDEMPOTENT: a second started fact for the same agent (a watchdog retry) must
  ///     not start a second poller appending the same messages twice, so a call while a loop
  ///     is live is a no-op.</para>
  ///     <para>A RESUMED AGENT IS FOLLOWED AGAIN (issue #330): the settle released the claim
  ///     (<see cref="MarkFinished"/>), so this starts a fresh loop. The new loop is CHAINED
  ///     behind the retired one, which is what stops the two from reading concurrently - the
  ///     cursor only advances once per read, so two overlapping reads would render one message
  ///     twice.</para>
  ///     <para>Only a host cell follows anything; a container cell's transcript is filled by
  ///     its own turn stream.</para></summary>
  public void StartFollowing()
  {
    if (!RunsInOwnHost)
    {
      return;
    }

    Task retired;
    int generation;
    lock (_followGate)
    {
      if (_followingGeneration != 0)
      {
        return; // a live loop owns the poller; a retried start is the same poller
      }

      generation = ++_followGeneration;
      _followingGeneration = generation;
      retired = _followLoop;
    }

    // A cell that is following again is by definition not finished (issue #330): the only way
    // back here after a settle is a RESUME, and a resumed agent is running.
    // THE CHIP AND THE REPORT RESET WITH IT (issue #331): a resumed agent has no phase beat and
    // no report yet, so leaving the previous run's chip and conclusion on the card would
    // describe a run that is over.
    if (IsFinished)
    {
      IsFinished = false;
      PhaseChip = CellPhaseChip.Starting;
      Report = null;
      ReportNotice = null;
      ViewModel.MarkSettled(false);
      RaiseStateChanged();
    }

    Task loop = FollowAfterAsync(retired, generation);
    lock (_followGate)
    {
      _followLoop = loop;
    }
  }

  /// <summary>Waits for the retired loop's last read to land, then runs the new one: the two
  ///     never overlap, so the shared cursor cannot count one message twice.</summary>
  /// <param name="retired">The previous loop, or a completed task when there was none.</param>
  /// <param name="generation">The generation the new loop owns.</param>
  private async Task FollowAfterAsync(Task retired, int generation)
  {
    await retired.ConfigureAwait(false);
    await FollowAsync(generation).ConfigureAwait(false);
  }

  /// <summary>The host agent's poll loop: one read per interval while it owns the current
  ///     generation. A store miss is not fatal - the next pass retries - because an agent that
  ///     has not persisted its first message yet is the ordinary state one tick after it
  ///     starts.
  ///     <para>The loop exits on its GENERATION, never on a shared flag: a resume re-arms
  ///     following immediately, and a flag would read as 'keep going' to the loop that is
  ///     already retiring.</para></summary>
  /// <param name="generation">The generation this loop owns.</param>
  private async Task FollowAsync(int generation)
  {
    // The first read is immediate: the cell appears with whatever the agent has already
    // persisted, rather than as an empty card for one interval.
    await ReadTranscriptAsync().ConfigureAwait(false);
    using PeriodicTimer timer = new(TranscriptPollInterval);
    while (await timer.WaitForNextTickAsync(CancellationToken.None).ConfigureAwait(false))
    {
      if (!OwnsFollow(generation))
      {
        return;
      }

      await ReadTranscriptAsync().ConfigureAwait(false);
    }
  }

  /// <summary>Whether <paramref name="generation"/> is still the generation following, which
  ///     is false once the cell settled or was detached.</summary>
  /// <param name="generation">The generation a loop owns.</param>
  private bool OwnsFollow(int generation)
  {
    lock (_followGate)
    {
      return _followingGeneration == generation;
    }
  }

  /// <summary>The one final read after settle (D4): the agent persists its last messages and
  ///     its report as it finishes, so the card's last content arrives AFTER the settled event.
  ///     Best effort - a store miss leaves the transcript as it stands.
  ///     <para>SERIALIZED WITH THE POLLER, not cancelled against it: a final read that raced an
  ///     in-flight poll read would observe the same message count twice and append the same
  ///     messages twice, because the cursor only advances once per read. <see cref="ReadOnceAsync"/>
  ///     is the one door.</para></summary>
  /// <summary>The one final read after settle (D4), then the agent's report (issue #331): the
  ///     report is read from the same store the transcript came from, so it needs no second
  ///     seam.
  ///     <para>THE CHIP REACHES <see cref="CellPhaseChip.Finished"/> ON BOTH OUTCOMES. A report
  ///     that could not be read is a fact the card states; it is never a reason to leave the
  ///     chip claiming the cell is still settling.</para></summary>
  private async Task FinishFollowingAsync()
  {
    await ReadOnceAsync().ConfigureAwait(false);
    await ReadReportAsync().ConfigureAwait(false);
  }

  /// <summary>Reads the agent's final report from the shared store (parent #325 D5), or records
  ///     why it could not be read.
  ///     <para>BEST EFFORT, STATED: a store miss or a failed read leaves
  ///     <see cref="ReportNotice"/> naming it, so the card never renders an empty panel that
  ///     would read as 'this agent concluded nothing'.</para></summary>
  private async Task ReadReportAsync()
  {
    (string? report, string? notice) = await LoadReportAsync().ConfigureAwait(false);

    // THE MUTATION IS MARSHALLED because the read ran off the UI thread and these are bound by
    // the view. The chip reaches Finished in the SAME marshal as the report, so a card can never
    // render 'Finished' beside a report panel that has not been filled yet.
    ViewModel.MarshalToUi(() =>
    {
      Report = report;
      ReportNotice = notice;
      PhaseChip = CellPhaseChip.Finished;
    });
  }

  /// <summary>Reads the report off the UI thread and reports what it found: the report and no
  ///     notice, or no report and the notice that says why.</summary>
  private async Task<(string? Report, string? Notice)> LoadReportAsync()
  {
    if (_store is null)
    {
      return (null, ReportUnavailableNotice);
    }

    Result<AgentRecord> record = await _store.GetAsync(RootId, CancellationToken.None)
        .ConfigureAwait(false);
    return record.IsSuccess ? (record.Value.FinalReport, null) : (null, ReportUnavailableNotice);
  }

  /// <summary>The line a card shows when its agent's report could not be read.</summary>
  internal const string ReportUnavailableNotice =
      "the final report could not be read from the store.";

  /// <summary>Runs one read, chained behind any read already in flight, so no two reads overlap.
  ///     <para>THE CHAIN IS THE SYNCHRONIZATION: the cursor and the append are both advanced
  ///     inside the read, so two concurrent reads are the only way to render one message twice,
  ///     and the chain makes that impossible without a lock, a flag, or a cancellation token
  ///     that the final read would have to win a race for.</para></summary>
  private Task ReadOnceAsync()
  {
    lock (_readGate)
    {
      _pendingRead = _pendingRead.ContinueWith(
          _ => ReadTranscriptAsync(),
          CancellationToken.None,
          TaskContinuationOptions.None,
          TaskScheduler.Default).Unwrap();
      return _pendingRead;
    }
  }

  /// <summary>Appends the messages the store holds that this cell has not rendered yet.
  ///     <para>INCREMENTAL BY INDEX: a running agent's transcript grows at its safe points, so
  ///     re-rendering the whole list every interval would rebuild every card and lose the
  ///     reader's place. The store's message list is append-only within a run, which is what
  ///     makes a count a sound cursor.</para>
  ///     <para>THE CURSOR ADVANCES WITH THE APPEND, never before it: the append is marshalled
  ///     onto the UI thread and awaited, so a message counted here has been handed to the
  ///     transcript before the next read can skip it. Advancing first would silently DROP
  ///     messages if the marshalled append never ran.</para>
  ///     <para>UI-THREAD CONTRACT: the append runs through the view-model's own UI marshal,
  ///     because entries are bound by the view.</para></summary>
  private async Task ReadTranscriptAsync()
  {
    if (_store is null)
    {
      return;
    }

    Result<IReadOnlyList<Message>> persisted = await _store.GetTranscriptAsync(RootId, CancellationToken.None)
        .ConfigureAwait(false);
    if (!persisted.IsSuccess)
    {
      return;
    }

    IReadOnlyList<Message> messages = persisted.Value;
    if (messages.Count <= _renderedCount)
    {
      return;
    }

    List<Message> fresh = [.. messages.Skip(_renderedCount)];
    await ViewModel.AppendPersistedMessagesAsync(fresh).ConfigureAwait(false);
    _renderedCount = messages.Count;
  }

  /// <summary>The store the host agent's transcript lands in. Null for a container cell, and
  ///     for a headless fixture that wired no store - such a cell renders nothing rather than
  ///     failing.</summary>
  private readonly IAgentStore? _store;

  /// <summary>How many of the agent's persisted messages this cell has rendered - the cursor
  ///     <see cref="ReadTranscriptAsync"/> resumes from.</summary>
  private int _renderedCount;

  /// <summary>Guards the poller's generation bookkeeping, so a start and a stop cannot
  ///     interleave into two live loops or none.</summary>
  private readonly Lock _followGate = new();

  /// <summary>The generation the live poll loop owns, or 0 when nothing is following. Written
  ///     by <see cref="StartFollowing"/> and cleared by <see cref="StopFollowing"/>.</summary>
  private int _followingGeneration;

  /// <summary>Every start's generation, so a retiring loop can tell it has been superseded.
  ///     Monotonic, incremented under <see cref="_followGate"/>.</summary>
  private int _followGeneration;

  /// <summary>The loop currently running, or the last one to finish. A new start chains behind
  ///     it so two loops never read the store at once.</summary>
  private Task _followLoop = Task.CompletedTask;

  /// <summary>Serializes the tail of the read chain. Held only for the assignment, so no read
  ///     ever runs under it.</summary>
  private readonly Lock _readGate = new();

  /// <summary>The tail of the read chain: the last read started, whether or not it finished.
  ///     A new read is chained behind it, so two reads never overlap.</summary>
  private Task _pendingRead = Task.CompletedTask;

  /// <summary>Applies one progress beat to the chip (issue #331). The chip is re-raised on
  ///     EVERY beat, including one that renders the same text, so a binding never looks stuck
  ///     for the whole of a long tool run.
  ///     <para>THE VOCABULARY IS CLOSED: the beat's wire label is translated by
  ///     <see cref="CellPhaseChip.FromLabel"/> and never echoed, so an unrecognised phase reads
  ///     <c>Thinking</c> rather than leaking wire text into the header.</para>
  ///     <para>A BEAT ON A SETTLED CARD IS IGNORED. Beats arrive on stream threads and a settle
  ///     can overtake a beat already in flight; honouring it would drag a finished card back to
  ///     a running phase.</para></summary>
  /// <param name="label">The beat's short phase label.</param>
  public void ApplyPhaseBeat(string? label)
  {
    if (IsFinished)
    {
      return;
    }

    string chip = CellPhaseChip.FromLabel(label);
    bool changed = !string.Equals(PhaseChip, chip, StringComparison.Ordinal);
    PhaseChip = chip;

    // AN UNCHANGED CHIP IS RE-RAISED HERE, because the generated setter suppresses an unchanged
    // value. The vocabulary is closed, so consecutive beats routinely render the same text - and
    // a header that stopped notifying through a long tool run would look frozen while the agent
    // is busy. Exactly one notification per beat either way.
    if (!changed)
    {
      OnPropertyChanged(nameof(PhaseChip));
    }
  }

  /// <summary>Marks this cell alerting and states why in its own transcript (parent #325 D12):
  ///     the outline is the at-a-glance half and the transcript line is the readable half.
  ///     <para>THE LINE GOES TO THIS CELL'S TRANSCRIPT because there is no parent (#299 D1) and
  ///     therefore no parent's transcript to write into; this is the only transcript the agent's
  ///     reader is looking at.</para>
  ///     <para>EXACTLY ONE LINE per alert, and an alert repeats while a run stays idle, so the
  ///     outline is set unconditionally while the line is written only when the alert is new.
  ///     </para></summary>
  /// <param name="notice">The one-line notice to append.</param>
  public void ApplyAlert(string notice)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(notice);
    bool wasAlerting = IsAlerting;
    IsAlerting = true;
    if (!wasAlerting)
    {
      ViewModel.MarshalToUi(() => ViewModel.AddSystemNotice(notice));
    }
  }

  /// <summary>Re-raises the derived state the cell's outline binds to.</summary>
  internal void RaiseStateChanged()
  {
    OnPropertyChanged(nameof(IsStreaming));
    OnPropertyChanged(nameof(IsIdle));
  }

  private void OnSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
  {
    if (e.PropertyName == nameof(AgentSessionViewModel.IsBusy))
    {
      RaiseStateChanged();
      return;
    }

    // A RESUME UN-FINISHES THE CARD (issue #330): the submit that resumed the agent marked it
    // running again, so the card must stop being a finished record and follow the new run. The
    // view-model reports the agent's run state; the card renders it.
    if (e.PropertyName == nameof(AgentSessionViewModel.IsSettled) && !ViewModel.IsSettled)
    {
      IsFinished = false;
      RaiseStateChanged();
      if (RunsInOwnHost)
      {
        StartFollowing();
      }
    }
  }
}
