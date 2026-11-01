using CommunityToolkit.Mvvm.ComponentModel;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>One agent cell inside a workspace tab (issue #307 D1): a session and its chat
///     surface, laid out beside its siblings.
///     <para>There is no orchestrator agent and therefore no privileged cell (D4/D9): every
///     cell owns its own chat input, so the user can steer any running agent or answer a
///     question from any waiting one. The cell carries no behaviour beyond that - the session
///     view-model owns the transcript, the turn loop, and the status bar.</para>
///     <para>A cell whose session closes PERSISTS as a finished card until the user dismisses
///     it (D7). It never reflows: in a grid the position carries meaning, and cell 3 was the
///     third thing you started.</para>
///     <para>TWO KINDS OF AGENT OCCUPY A CELL (issue #329). A SESSION cell is a root
///     conversation this app owns: it holds the AgentSession container and the app drives its
///     turns. A SPAWNED cell is an agent started by agent.spawn, which runs in its OWN host
///     process and is owned by nothing here: the cell learns it exists from
///     ChildStartedEvent and follows it by reading the shared store, so Container is null for
///     exactly that kind. Both occupy a grid position and render through the same template,
///     which is why they are one type.</para></summary>
internal sealed partial class AgentCellViewModel : ObservableObject
{
  /// <summary>How often a running spawned agent's transcript is re-read from the shared
  ///     store (parent #325 D4). The child runs in another process and persists at its own
  ///     safe points, so the store is the only seam that sees its messages.</summary>
  internal static readonly TimeSpan SpawnedTranscriptInterval = TimeSpan.FromMilliseconds(500);

  public AgentCellViewModel(AgentSession session, AgentSessionViewModel sessionVm)
  {
    Container = session ?? throw new ArgumentNullException(nameof(session));
    ViewModel = sessionVm ?? throw new ArgumentNullException(nameof(sessionVm));
    RootId = session.RootId;
    // The outline follows the turn (D5): a stalled or waiting agent must be visible without
    // reading its transcript, so the cell watches the session's busy flag.
    ViewModel.PropertyChanged += OnSessionPropertyChanged;
  }

  /// <summary>Builds a cell over a SPAWNED agent this app does not own (issue #329): the
  ///     child runs in its own host process, so there is no container here to hold - the cell
  ///     follows the agent through the shared store and the event stream instead.
  ///     <para>Nothing polls until <see cref="StartFollowing"/> is called, so a cell built
  ///     for a settled agent, or by a test, performs no reads at all.</para></summary>
  /// <param name="childId">The spawned agent's id, as the started event named it.</param>
  /// <param name="label">The spawn label the child's record carries (D9: the UI shows a
  ///     label, the database keeps the id).</param>
  /// <param name="store">The shared agent store, which is where the child's transcript
  ///     lands.</param>
  /// <param name="sessionVm">The view-model rendering the child's transcript.</param>
  public AgentCellViewModel(AgentId childId, string label, IAgentStore? store,
      AgentSessionViewModel sessionVm)
  {
    ArgumentNullException.ThrowIfNull(sessionVm);
    RootId = childId;
    Label = label;
    ViewModel = sessionVm;
    _store = store;
    IsSpawned = true;
    ViewModel.PropertyChanged += OnSessionPropertyChanged;
  }

  /// <summary>The session container this cell owns, or null for a spawned agent (issue
  ///     #329): the app holds no container for an agent running in its own host process.
  ///     <para>Every path that disposes a container or completes a session must check
  ///     <see cref="IsSpawned"/> first - completing a spawned child's row here would tell
  ///     the store that a live run had finished.</para></summary>
  public AgentSession? Container { get; }

  /// <summary>The agent this cell shows, whether it is a session or a spawned child.
  ///     <para>THIS IS THE CELL'S IDENTITY: the grid never keys a cell by position (D7 - the
  ///     position is the spawn order), so every lookup goes through this id.</para></summary>
  public AgentId RootId { get; }

  /// <summary>The spawn label the child's record carries. Null for a session cell, whose
  ///     caption is its workspace directory name.</summary>
  public string? Label { get; }

  /// <summary>True when this cell follows an agent agent.spawn started rather than a session
  ///     the tab was opened for (issue #329).</summary>
  public bool IsSpawned { get; }

  public AgentSessionViewModel ViewModel { get; }

  /// <summary>True once this cell's agent has closed or settled. The cell stays in the grid
  ///     as a finished card (D7) - it is not removed and the remaining cells do not
  ///     reflow.</summary>
  [ObservableProperty]
  public partial bool IsFinished { get; private set; }

  /// <summary>Whether this cell is the one the user is working in. Set by the tab, never by
  ///     the cell: focus is a property of the grid, not of the agent.</summary>
  [ObservableProperty]
  public partial bool IsFocused { get; internal set; }

  /// <summary>Whether this cell's agent is currently streaming a turn. Every cell renders
  ///     LIVE (D3), so this drives the outline rather than whether the cell renders at all
  ///     (D5).
  ///     <para>A spawned agent is streaming while it has not settled: this app never sees its
  ///     turn boundaries, only that it started and that it finished.</para></summary>
  public bool IsStreaming => IsSpawned ? !IsFinished : ViewModel.IsBusy;

  /// <summary>Whether the cell renders the distinct idle outline (D5). A finished card is
  ///     idle by definition, and a busy agent never is.</summary>
  public bool IsIdle => !IsFinished && !IsStreaming;

  /// <summary>The cell's caption. A spawned agent shows the label its spawn named (D9: the
  ///     id is the database's, the label is the user's), falling back to the short id for a
  ///     spawn that named none.
  ///     <para>A session cell shows its workspace's directory name, which is what a user
  ///     recognises, falling back to the session id when the root has no file name (a drive
  ///     root such as C:\).</para></summary>
  public string Title
  {
    get
    {
      if (IsSpawned)
      {
        return string.IsNullOrWhiteSpace(Label) ? RootId.ToString()[..8] : Label;
      }

      string root = Container?.WorkspaceRoot ?? ViewModel.WorkspaceRoot;
      string name = Path.GetFileName(
          root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
      return string.IsNullOrEmpty(name) ? RootId.ToString()[..8] : name;
    }
  }

  /// <summary>Marks the cell finished. The session is already gone (or going); nothing here
  ///     disposes it, because the shell owns that teardown and must run it exactly once.
  ///     Calling again is a no-op, so a close path and a shutdown path cannot disagree.
  ///     <para>A spawned cell stops following its agent here: the one final read (D4) has
  ///     already landed, and a settled agent's store rows do not change again.</para></summary>
  public void MarkFinished()
  {
    if (IsFinished)
    {
      return;
    }

    IsFinished = true;
    if (IsSpawned)
    {
      // The terminal row and the report land AFTER the settled event, so the final read is
      // what puts them in the card.
      _ = FinishSpawnedAsync();
    }

    RaiseStateChanged();
  }

  /// <summary>Detaches the session subscription and stops the spawned-agent poller. Called
  ///     when the tab drops the cell, so a dismissed card cannot keep raising property
  ///     changes for a cell nothing renders.</summary>
  public void Detach()
  {
    ViewModel.PropertyChanged -= OnSessionPropertyChanged;
    StopFollowing();
  }

  /// <summary>Stops the spawned agent's poller. Called from <see cref="Detach"/> and from the
  ///     tab's teardown, so a dismissed card cannot keep reading the store.
  ///     <para>A FLAG, NOT A CANCELLATION SOURCE: the loop holds no unmanaged state and its
  ///     next tick is at most one interval away, so a token would buy a latency the cell does
  ///     not need while making the cell itself disposable - and therefore obliging every
  ///     caller to own its lifetime. The loop re-reads the flag on every pass.</para></summary>
  public void StopFollowing() => _following = false;

  /// <summary>Starts following a spawned agent (issue #329): reads its transcript now, then
  ///     every <see cref="SpawnedTranscriptInterval"/> until the cell is finished (parent
  ///     #325 D4).
  ///     <para>IDEMPOTENT: a second started event for the same agent (a watchdog retry) must
  ///     not start a second poller appending the same messages twice.</para>
  ///     <para>Only a spawned cell follows anything; a session cell's transcript is filled by
  ///     its own turn stream.</para></summary>
  public void StartFollowing()
  {
    if (!IsSpawned || _following)
    {
      return;
    }

    _following = true;
    _ = FollowAsync();
  }

  /// <summary>The spawned agent's poll loop: one read per interval while it runs. A store
  ///     miss is not fatal - the next pass retries - because a child that has not persisted
  ///     its first message yet is the ordinary state one tick after it starts.</summary>
  private async Task FollowAsync()
  {
    // The first read is immediate: the cell appears with whatever the child has already
    // persisted, rather than as an empty card for one interval.
    await ReadTranscriptAsync(CancellationToken.None).ConfigureAwait(false);
    using PeriodicTimer timer = new(SpawnedTranscriptInterval);
    while (await timer.WaitForNextTickAsync(CancellationToken.None).ConfigureAwait(false))
    {
      if (!_following || IsFinished)
      {
        return;
      }

      await ReadTranscriptAsync(CancellationToken.None).ConfigureAwait(false);
    }
  }

  /// <summary>The one final read after settle (D4): the child persists its last messages
  ///     and its report as it finishes, so the card's last content arrives AFTER the settled
  ///     event. Best effort - a store miss leaves the transcript as it stands.
  ///     <para>The poller is stopped through <see cref="Detach"/>, not here: the settled event
  ///     can arrive while a read is in flight, and a cancel from this path would race it. The
  ///     loop already exits on <see cref="IsFinished"/> at its next tick.</para></summary>
  private Task FinishSpawnedAsync() => ReadTranscriptAsync(CancellationToken.None);

  /// <summary>Appends the messages the store holds that this cell has not rendered yet.
  ///     <para>INCREMENTAL BY INDEX: a running agent's transcript grows at its safe points,
  ///     so re-rendering the whole list every interval would rebuild every card and lose the
  ///     reader's place. The store's message list is append-only within a run, which is what
  ///     makes a count a sound cursor.</para>
  ///     <para>UI-THREAD CONTRACT: the append runs through the view-model's own UI marshal,
  ///     because entries are bound by the view.</para></summary>
  private async Task ReadTranscriptAsync(CancellationToken ct)
  {
    if (_store is null)
    {
      return;
    }

    Result<IReadOnlyList<Message>> persisted = await _store.GetTranscriptAsync(RootId, ct).ConfigureAwait(false);
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
    _renderedCount = messages.Count;
    ViewModel.AppendSpawnedMessages(fresh);
  }

  /// <summary>The store the spawned agent's transcript lands in. Null for a session cell,
  ///     and for a headless fixture that wired no store - such a cell renders nothing rather
  ///     than failing.</summary>
  private readonly IAgentStore? _store;

  /// <summary>How many of the spawned agent's persisted messages this cell has rendered -
  ///     the cursor <see cref="ReadTranscriptAsync"/> resumes from.</summary>
  private int _renderedCount;

  /// <summary>Whether the spawned agent's poll loop should keep reading. Cleared by
  ///     <see cref="StopFollowing"/>; the loop observes it on every tick.</summary>
  private bool _following;

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
    }
  }
}
