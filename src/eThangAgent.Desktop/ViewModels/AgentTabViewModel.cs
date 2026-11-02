using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.StateDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>One open workspace tab in the shell (issue #307 D1): the tab IS the workspace,
///     and what lives inside it is 1..N agent cells.
///     <para>Before #307 a tab was one session and one chat. It is now a grid: one agent looks
///     exactly like the old tab, two split, three tile. The tab keeps the workspace root and
///     the provider, and the SESSION is what became plural.</para>
///     <para>The workbench rail is the tab's, not a cell's (D6): its views are workspace-scoped
///     (plans, git, PRs, database), so they do not belong to any one agent.</para>
///     <para><see cref="ViewModel"/> and <see cref="Container"/> name the PRIMARY cell - the
///     one the tab was opened for. They are the pre-#307 surface, kept because a great deal of
///     the shell addresses 'the tab's session' and, with one cell, that is exactly right.</para>
///     <para>EVERY CELL KEEPS ITS OWN SUBSCRIPTIONS (issue #329). A tab can hold several
///     sessions, each with its own event stream, so the agent-cell feed and the chain advance
///     are held PER SESSION and accumulated rather than replaced: a tab that gained a second
///     cell must still hear the first session's facts.</para></summary>
internal sealed partial class AgentTabViewModel : ObservableObject
{
  public AgentTabViewModel(AgentSession session, AgentSessionViewModel sessionVm)
      : this(session, sessionVm, workbench: null)
  {
  }

  /// <summary>Builds the tab around its primary cell, optionally sharing a workbench the
  ///     caller already built (the shell builds one per tab and hands it to every session
  ///     view-model in it, so the rail is workspace-scoped and survives a cell swap).</summary>
  public AgentTabViewModel(AgentSession session, AgentSessionViewModel sessionVm,
      WorkbenchViewModel? workbench)
  {
    ArgumentNullException.ThrowIfNull(session);
    ArgumentNullException.ThrowIfNull(sessionVm);
    Workbench = workbench ?? sessionVm.Workbench;
    WorkspaceId = session.Services.GetService(typeof(IWorkspaceContext)) is IWorkspaceContext context
        ? context.WorkspaceId
        : null;
    AgentCellViewModel primary = new(session, sessionVm);
    primary.PropertyChanged += OnCellPropertyChanged;
    Cells = [primary];
    Cells.CollectionChanged += OnCellsChanged;
    Title = Path.GetFileName(
        session.WorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
  }

  /// <summary>The tab's live session container: the earliest cell whose session this app still
  ///     holds. With one cell this is the tab's session; with several it is the first one that
  ///     has not been retired.
  ///     <para>DELEGATED, not stored: every route that starts a session appends a cell and
  ///     retires the one it supersedes (issue #332), so a stored copy would name a session the
  ///     tab no longer holds.</para>
  ///     <para>WHY THE FIRST LIVE CELL AND NOT SIMPLY CELL ZERO: a superseded agent becomes a
  ///     finished card and its container is disposed. The card stays - D7 keeps the position -
  ///     but the tab must not resolve 'its session' to a disposed provider.</para>
  ///     <para>THE FIRST CELL IS ALWAYS A CONTAINER CELL (D13): agents running in their own hosts
  ///     append after it, and a host cell never becomes the tab's own session.</para></summary>
  public AgentSession Container => Cells.Select(cell => cell.Container).FirstOrDefault(s => s is not null)
      ?? throw new InvalidOperationException("the tab must hold at least one live container cell.");

  /// <summary>The live cell's view-model (see <see cref="Container"/>): the cell whose session
  ///     the tab is anchored on, so the pre-#307 surface keeps addressing a LIVE conversation
  ///     even after an earlier cell became a finished card.
  ///     <para>NOT SIMPLY CELL ZERO (issue #332). Every route that starts a session appends a
  ///     cell - a chain advance, a compaction handoff, and `/new` alike - so the first cell is
  ///     the tab's first agent and nothing more, and it becomes a finished card the moment a
  ///     later agent supersedes it. Resolving 'the tab's session' to it would point the
  ///     statusline, the model controls, and the rail at a session whose container is gone.
  ///     </para>
  ///     <para>A RETIRED CELL IS THE MARKER FOR THAT: it has no container left to address, so
  ///     the first cell that still has one is the one the tab is about. Cell zero is the
  ///     fallback only when every cell is retired, which is a tab nothing can be done in.</para></summary>
  public AgentSessionViewModel ViewModel => Cells.Select(cell => cell.ViewModel)
      .FirstOrDefault(vm => !vm.IsRetired) ?? Cells[0].ViewModel;

  /// <summary>The workspace this tab shows, as the stable identity its durable rows key on.
  ///     Null when the tab was built over a session whose container carries no workspace
  ///     context (a headless stub).</summary>
  public string? WorkspaceId { get; }

  /// <summary>The workspace tab's agent cells, in the order they were started (D7: position
  ///     carries meaning, so a finished cell keeps its place rather than reflowing).</summary>
  public ObservableCollection<AgentCellViewModel> Cells { get; }

  /// <summary>The tab's workbench rail (D6): workspace-scoped, shared by every cell, and
  ///     unchanged by a cell's arrival or departure.</summary>
  public WorkbenchViewModel Workbench { get; }

  public string Title { get; }

  public string Subtitle => ViewModel.WorkspaceRoot;

  /// <summary>Whether this tab shows the workspace identified by <paramref name="workspaceId"/>.
  ///     The identity is compared when the tab carries one, and the directory path when it does
  ///     not, so a tab is still resolvable in a host that wired no workspace context.</summary>
  /// <param name="workspaceId">The workspace an agent's own record names.</param>
  public bool OwnsWorkspace(string workspaceId)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
    return WorkspaceId is { } id
        ? string.Equals(id, workspaceId, StringComparison.Ordinal)
        : WorkspaceIdentity.PathsEqual(Container.WorkspaceRoot, workspaceId);
  }

  /// <summary>Whether this tab is the shell's selected one. Drives the keep-alive
  ///     content panel's visibility: every open tab's view stays built; only the
  ///     selected one is visible. Set by MainViewModel.OnSelectedTabChanged.</summary>
  [ObservableProperty]
  public partial bool IsSelected { get; set; }

  /// <summary>The tab's unread-steering badge, attached by the shell against the
  ///     session's agent-event stream (W4.4). Null when no stream is wired
  ///     (headless/test hosts construct nothing).</summary>
  [ObservableProperty]
  public partial TabBadgeViewModel? Badge { get; private set; }

  /// <summary>How many of this workspace's chains are PARKED - waiting for a human (issue #312
  ///     D6). Mirrored from the rail's own count, so the two can never disagree.
  ///     <para>WHY IT IS ON THE TAB AND NOT IN A CELL'S STATUS BAR: the rail is workspace-scoped
  ///     (D6), so the count is a fact about the WORKSPACE. A cell status bar is per AGENT, and a
  ///     tab with three cells would then show the same number three times - three places to
  ///     disagree instead of one to trust. The tab's header strip is always visible whenever the
  ///     tab is, which is what D6 actually requires: a chain waiting for a human cannot depend on
  ///     the user having opened a particular view.</para></summary>
  [ObservableProperty]
  public partial int ParkedChains { get; private set; }

  /// <summary>Whether the parked badge shows at all.</summary>
  public bool HasParkedChains => ParkedChains > 0;

  /// <summary>Adds a cell for a session this app OWNS (issue #307 D2). The cell is appended,
  ///     so the grid's existing cells keep their positions.
  ///     <para>EVERY SESSION THIS APP STARTS IN A TAB COMES THROUGH HERE (issue #332): a chain
  ///     step, a compaction handoff, and `/new` all append, because all three start an agent
  ///     beside the one that was there rather than restarting that one in place. The "+"
  ///     control comes through here too (issue #333), superseding nothing.</para>
  ///     <para>NOTHING HERE SELECTS OR SCROLLS (D2): the cell arrives at the end of the row and
  ///     the user clicks into it. A handoff that arrived mid-task is exactly the case where
  ///     stealing the view would be worst.</para></summary>
  /// <param name="session">The session this app owns and just started.</param>
  /// <param name="sessionVm">The view-model rendering that session.</param>
  /// <param name="placement">The chain step it executes, or null when it is not a chain step.</param>
  /// <param name="label">The caption the cell shows (issue #338), or null for the workspace
  ///     directory name a container cell otherwise takes. A model's launch states one because a
  ///     caller that started several agents must be able to say which one it meant.</param>
  public AgentCellViewModel AddCell(AgentSession session, AgentSessionViewModel sessionVm,
      ChainPlacement? placement = null, string? label = null)
  {
    AgentCellViewModel cell = new(session, sessionVm, placement, label);
    cell.PropertyChanged += OnCellPropertyChanged;
    Cells.Add(cell);
    return cell;
  }

  /// <summary>Adds a cell for an agent running in its OWN host process (issue #329): nothing
  ///     here owns its container, so the cell carries none - it follows the agent through the
  ///     shared store and the event stream.
  ///     <para>APPENDED, in start order, and NOT SELECTED (D2/D13): a new cell never steals the
  ///     view. Nothing here focuses, scrolls, or changes <see cref="IsSelected"/>; the user
  ///     clicks a cell to work in it.</para>
  ///     <para>ONE CELL PER AGENT: a repeated started fact for an agent this tab already shows
  ///     is a restart, not a second agent, so the existing cell is returned rather than a
  ///     duplicate appended.</para></summary>
  /// <param name="agentId">The agent's id.</param>
  /// <param name="label">The label its record carries, or empty.</param>
  /// <param name="placement">The chain step it executes, or null when started by hand.</param>
  /// <param name="store">The shared store its transcript lands in.</param>
  /// <param name="sessionVm">The view-model rendering it.</param>
  public AgentCellViewModel AddAgentCell(AgentId agentId, string label, ChainPlacement? placement,
      IAgentStore? store, AgentSessionViewModel sessionVm)
  {
    if (FindCell(agentId) is { } existing)
    {
      return existing;
    }

    AgentCellViewModel cell = new(agentId, label, store, sessionVm, placement);
    cell.PropertyChanged += OnCellPropertyChanged;
    Cells.Add(cell);
    cell.StartFollowing();
    return cell;
  }

  /// <summary>Starts one more agent in this workspace when the user clicks "+" (issue #333).
  ///     Null when the tab has no shell attached, which is when <see cref="CanAddAgent"/> is
  ///     false and the control does not render.</summary>
  public Func<Task>? AddAgentRequested { get; private set; }

  /// <summary>Sets the "+" launch for this tab. Calling again replaces the request; a tab
  ///     holds at most one, so a click starts at most one agent.</summary>
  /// <param name="request">Starts one agent in this tab's workspace.</param>
  public void AttachAddAgent(Func<Task> request)
  {
    ArgumentNullException.ThrowIfNull(request);
    AddAgentRequested = request;
    OnPropertyChanged(nameof(CanAddAgent));
  }

  /// <summary>Whether a "+" click would start an agent: true once the shell has attached a
  ///     request. The control renders only while this is true.</summary>
  public bool CanAddAgent => AddAgentRequested is not null;

  /// <summary>Why the last "+" click started nothing (issue #333), or null when it started an
  ///     agent. It lives on the tab rather than in a cell's transcript because a refused click
  ///     produces no cell. Cleared by the next click.</summary>
  [ObservableProperty]
  public partial string? AddAgentNotice { get; private set; }

  /// <summary>Whether the tab renders the "+" refusal.</summary>
  public bool HasAddAgentNotice => !string.IsNullOrWhiteSpace(AddAgentNotice);

  /// <summary>Re-raises the derived visibility flag when the refusal moves: it is a computed
  ///     property, so the generated setter's own notification says nothing about it.</summary>
  /// <param name="value">The notice just assigned.</param>
  partial void OnAddAgentNoticeChanged(string? value) => OnPropertyChanged(nameof(HasAddAgentNotice));

  /// <summary>Records why a "+" click started no agent, so the tab renders the reason.
  ///     </summary>
  /// <param name="message">Why the agent was not started.</param>
  public void ReportAddAgentFailure(string message)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(message);
    AddAgentNotice = message;
  }

  /// <summary>Clears the previous "+" refusal, so no stale reason renders beside a later
  ///     agent.</summary>
  public void ClearAddAgentNotice() => AddAgentNotice = null;

  /// <summary>Retires one cell's session (issue #332): the card stops naming the container the
  ///     shell is about to dispose, stays where it is as a finished record, and the tab
  ///     re-raises the surfaces that resolve THROUGH the cells.
  ///     <para>ONE CALL BECAUSE THE TWO HALVES MUST NOT DRIFT: a card marked finished while
  ///     still holding a disposed provider answers every later lookup with a dead session, and
  ///     a container released without the card being marked finished leaves a live-looking
  ///     card over nothing.</para>
  ///     <para>THE RE-RAISED PROPERTIES ARE THE TAB'S OWN: <see cref="Container"/> and
  ///     <see cref="ViewModel"/> are computed over the cells, so retiring the cell they named
  ///     moves them and every binding on them has to hear it.</para></summary>
  /// <param name="cell">The cell whose session is being retired.</param>
  public void RetireCell(AgentCellViewModel cell)
  {
    ArgumentNullException.ThrowIfNull(cell);
    cell.MarkFinished();
    cell.RetireContainer();
    OnPropertyChanged(nameof(Container));
    OnPropertyChanged(nameof(ViewModel));
    OnPropertyChanged(nameof(Subtitle));
  }

  /// <summary>Finds the cell showing <paramref name="agentId"/>, or null when this tab shows
  ///     none. The one lookup every caller uses, because a cell's identity is its agent rather
  ///     than its position (D7).</summary>
  public AgentCellViewModel? FindCell(AgentId agentId)
    => Cells.FirstOrDefault(c => c.RootId == agentId);

  /// <summary>Removes a FINISHED cell the user dismissed (D7). A running cell is refused:
  ///     dismissing it would orphan a live session with no surface.
  ///     <para>THE LAST CELL IS REFUSED TOO (issue #332): the tab resolves its own session
  ///     through the cells, so removing the only cell would leave a tab whose
  ///     <see cref="Container"/> names nothing. CELL ZERO IS NOT PROTECTED - every route that
  ///     starts a session appends, so the first cell is an ordinary agent's card and the user
  ///     closes it like any other.</para>
  ///     <para>Returns false when the cell was not dismissed, so the caller can say why.</para></summary>
  public bool DismissCell(AgentCellViewModel cell)
  {
    ArgumentNullException.ThrowIfNull(cell);
    if (!cell.IsFinished || Cells.Count <= 1)
    {
      return false;
    }

    cell.PropertyChanged -= OnCellPropertyChanged;
    cell.Detach();
    return Cells.Remove(cell);
  }

  /// <summary>Marks the cell owning <paramref name="rootId"/> finished (its session closed).
  ///     The cell stays in the grid as a card (D7); nothing is removed.
  ///     <para>Returns the cell, or null when no cell owns that session.</para></summary>
  public AgentCellViewModel? MarkSessionFinished(AgentId rootId)
  {
    AgentCellViewModel? cell = FindCell(rootId);
    cell?.MarkFinished();
    return cell;
  }

  /// <summary>Whether every cell in the tab is finished: the tab is then only a record of
  ///     what ran, which the shell may close as a whole.</summary>
  public bool IsFullyFinished => Cells.Count == 0 || Cells.All(c => c.IsFinished);

  /// <summary>Re-raises the grid's own derived state when a cell's outline inputs move.
  ///     The cell raises its own properties; the tab mirrors the ones the grid binds to.</summary>
  private void OnCellPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
  {
    if (e.PropertyName is nameof(AgentCellViewModel.IsFinished) or nameof(AgentCellViewModel.IsStreaming))
    {
      OnPropertyChanged(nameof(IsFullyFinished));
    }
  }

  private void OnCellsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    => OnPropertyChanged(nameof(IsFullyFinished));

  /// <summary>Subscribes the badge to this session's event stream. Called once by the shell
  ///     after the tab is created; calling again detaches the prior badge.
  ///     The render callback posts through the UI Dispatcher - events arrive on stream
  ///     threads, transcript/badge mutation is UI-thread-only.</summary>
  public void AttachBadge(IAgentEvents? events)
  {
    Badge?.Dispose();
    Badge = TabBadgeViewModel.Attach(events, Container.RootId, apply => count =>
      Avalonia.Threading.Dispatcher.UIThread.Post(() => apply(count)));
  }

  /// <summary>The tab's statusline branch watcher (issue 1). Attached by the shell's
  ///     attach path when the session exposes a git query seam; disposed on close.
  ///     Null when the shell wired none (headless/test hosts construct nothing).</summary>
  private GitBranchWatcher? _branchWatcher;

  /// <summary>Per-session leases for the agent-cell feed and the chain advance (issue #329).
  ///     KEYED BY SESSION, not held as one slot: a tab holds several sessions, each with its
  ///     own stream, and replacing the slot would silently stop the first session's facts.
  ///     </summary>
  private readonly Dictionary<AgentId, IDisposable> _agentCellLeases = [];

  private readonly Dictionary<AgentId, IDisposable> _chainAdvanceLeases = [];

  /// <summary>Attaches the agent-cell feed for ONE session of this tab. Calling again for the
  ///     same session disposes that session's prior lease, so a re-attach cannot leave two
  ///     subscribers appending a cell for one agent - and a DIFFERENT session's lease is left
  ///     alone, which is what keeps a multi-agent tab hearing every one of its streams.</summary>
  /// <param name="sessionId">The session whose stream the lease covers.</param>
  /// <param name="lease">The subscription.</param>
  public void AttachAgentCells(AgentId sessionId, IDisposable lease)
  {
    ArgumentNullException.ThrowIfNull(lease);
    if (_agentCellLeases.Remove(sessionId, out IDisposable? prior))
    {
      prior.Dispose();
    }

    _agentCellLeases[sessionId] = lease;
  }

  /// <summary>Disposes every agent-cell feed (tab close). Safe when none was attached.</summary>
  public void DetachAgentCells()
  {
    foreach (IDisposable lease in _agentCellLeases.Values)
    {
      lease.Dispose();
    }

    _agentCellLeases.Clear();
  }

  /// <summary>Attaches the chain-advance subscription for one session's stream. Calling again
  ///     for the same session disposes that session's prior lease first, so a cell swap cannot
  ///     leave two subscribers advancing the same chain.</summary>
  /// <param name="sessionId">The session whose stream the lease covers.</param>
  /// <param name="lease">The subscription.</param>
  public void AttachChainAdvance(AgentId sessionId, IDisposable lease)
  {
    ArgumentNullException.ThrowIfNull(lease);
    if (_chainAdvanceLeases.Remove(sessionId, out IDisposable? prior))
    {
      prior.Dispose();
    }

    _chainAdvanceLeases[sessionId] = lease;
  }

  /// <summary>Disposes every chain-advance subscription (tab close). Safe when none was
  ///     attached.</summary>
  public void DetachChainAdvance()
  {
    foreach (IDisposable lease in _chainAdvanceLeases.Values)
    {
      lease.Dispose();
    }

    _chainAdvanceLeases.Clear();
  }

  /// <summary>Mirrors the rail's parked-chain count into <see cref="ParkedChains"/> (issue
  ///     #312 D6). Called by the shell at attach; idempotent because the subscription replaces
  ///     the previous one rather than adding a second.
  ///     <para>The render posts through the UI Dispatcher: the rail's refresh completes on pool
  ///     threads, and the badge is bound by the view.</para></summary>
  /// <param name="chains">The tab's chain view, whose count is the one source of truth.</param>
  public void AttachParkedChains(ChainsViewViewModel? chains)
  {
    _parkedChainsFrom?.ParkedCountChanged -= HandleParkedCountChanged;
    _parkedChainsFrom = chains;
    if (chains is null)
    {
      ParkedChains = 0;
      return;
    }

    chains.ParkedCountChanged += HandleParkedCountChanged;
    ParkedChains = chains.ParkedCount;
  }

  /// <summary>The view whose count this tab mirrors, so a re-attach can unsubscribe.</summary>
  private ChainsViewViewModel? _parkedChainsFrom;

  private void HandleParkedCountChanged(int count)
    => Avalonia.Threading.Dispatcher.UIThread.Post(() => ParkedChains = count);

  /// <summary>Re-raises the derived visibility flag whenever the count moves, so the view never
  ///     has to compare the number itself.
  ///     <para>The generator's own hook, so every assignment - the attach's initial read and the
  ///     mirrored change alike - goes through one place.</para></summary>
  /// <param name="value">The new parked count.</param>
  partial void OnParkedChainsChanged(int value)
  {
    _ = value;
    OnPropertyChanged(nameof(HasParkedChains));
  }

  /// <summary>Attaches the statusline branch watcher over the tab's workspace.
  ///     Calling again disposes the prior watcher first.</summary>
  public void AttachBranchWatcher(IGitQueryAccess? git)
  {
    _branchWatcher?.Dispose();
    _branchWatcher = git is null ? null : new GitBranchWatcher(Container.WorkspaceRoot, git, ViewModel.Status);
  }

  /// <summary>Disposes the branch watcher (tab close). Safe when none was attached.</summary>
  public void DetachBranchWatcher() => _branchWatcher?.Dispose();
}
