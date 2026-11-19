using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
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
  ///     view-model in it, so the rail is workspace-scoped and survives a cell swap).
  ///     <para>The tab is named after its workspace directory.</para></summary>
  public AgentTabViewModel(AgentSession session, AgentSessionViewModel sessionVm,
      WorkbenchViewModel? workbench)
      : this(session, sessionVm, workbench, WorkspaceFolderName(session))
  {
  }

  /// <summary>Builds the tab around its primary cell with the name the tab strip shows.
  ///     <para>The name is the caller's: a window's tabs all belong to one workspace, so the
  ///     directory names none of them.</para></summary>
  public AgentTabViewModel(AgentSession session, AgentSessionViewModel sessionVm,
      WorkbenchViewModel? workbench, string title)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(title);
    ArgumentNullException.ThrowIfNull(session);
    ArgumentNullException.ThrowIfNull(sessionVm);
    Workbench = workbench ?? sessionVm.Workbench;
    WorkspaceId = session.Services.GetService(typeof(IWorkspaceContext)) is IWorkspaceContext context
        ? context.WorkspaceId
        : null;
    WorkspaceRoot = session.WorkspaceRoot;
    AgentCellViewModel primary = new(session, sessionVm);
    primary.PropertyChanged += OnCellPropertyChanged;
    Cells = [primary];
    Cells.CollectionChanged += OnCellsChanged;
    Title = title;
  }

  /// <summary>The last segment of a workspace directory, which is what a tab built without a
  ///     name of its own is called.</summary>
  /// <param name="session">The session whose workspace names the tab.</param>
  private static string WorkspaceFolderName(AgentSession session)
  {
    ArgumentNullException.ThrowIfNull(session);
    return Path.GetFileName(
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
  public AgentSession Container => LiveContainer
      ?? throw new InvalidOperationException("the tab must hold at least one live container cell.");

  /// <summary>The live container, or null when the tab holds no cell whose session this app
  ///     still holds - the state a tab reaches when the user closes its last card.
  ///     <para>THE NULLABLE HALF IS THE OPT-IN: <see cref="Container"/> keeps throwing for the
  ///     paths that genuinely cannot proceed without a session, and the few that can - the
  ///     workspace identity, the branch watcher - read this instead.</para></summary>
  public AgentSession? LiveContainer
    => Snapshot().Select(cell => cell.Container).FirstOrDefault(s => s is not null);

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
  public AgentSessionViewModel ViewModel => LiveViewModel
      ?? throw new InvalidOperationException("the tab must hold at least one agent cell.");

  /// <summary>The live cell's view-model, or null when every cell is retired or the tab holds
  ///     none (see <see cref="LiveContainer"/>).</summary>
  public AgentSessionViewModel? LiveViewModel
    => Snapshot().Select(cell => cell.ViewModel).FirstOrDefault(vm => !vm.IsRetired);

  /// <summary>The workspace this tab shows, as the stable identity its durable rows key on.
  ///     Null when the tab was built over a session whose container carries no workspace
  ///     context (a headless stub).</summary>
  public string? WorkspaceId { get; }

  /// <summary>The workspace tab's agent cells, in the order they were started (D7: position
  ///     carries meaning, so a finished cell keeps its place rather than reflowing).</summary>
  public ObservableCollection<AgentCellViewModel> Cells { get; }

  /// <summary>Serializes every read and write of <see cref="Cells"/>.
  ///     <para>THE COLLECTION IS APPENDED TO OFF THE UI THREAD AND ENUMERATED ON IT. A cell
  ///     lands after an asynchronous store read (<c>AgentCellSubscriber</c>), while the grid,
  ///     the shell's lookups, and a waiting test all walk the collection. Enumerating an
  ///     <c>ObservableCollection</c> while another thread appends throws
  ///     <c>InvalidOperationException</c>, and the subscriber contains that fault - so the
  ///     started fact is lost and no cell ever appears for that agent.</para></summary>
  private readonly Lock _cellsGate = new();

  /// <summary>A copy of <see cref="Cells"/> taken under the gate, which is the only safe way to
  ///     walk it: the live collection can be appended to from another thread at any moment.
  ///     </summary>
  internal AgentCellViewModel[] Snapshot()
  {
    lock (_cellsGate)
    {
      return [.. Cells];
    }
  }

  /// <summary>The tab's workbench rail (D6): workspace-scoped, shared by every cell, and
  ///     unchanged by a cell's arrival or departure.</summary>
  public WorkbenchViewModel Workbench { get; }

  /// <summary>The name the tab strip shows.</summary>
  public string Title { get; }

  /// <summary>The workspace directory this tab shows. STORED, not derived from a cell: a tab
  ///     whose last card was closed still knows where its next agent goes.</summary>
  public string WorkspaceRoot { get; }

  public string Subtitle => WorkspaceRoot;

  /// <summary>What the tab's header states on hover: the workspace it belongs to, the
  ///     provider it is wired for, and the session it holds. A tab holding no live session
  ///     states its workspace alone.</summary>
  public string Tooltip
  {
    get
    {
      AgentSession? live = LiveContainer;
      return live is null
          ? WorkspaceRoot
          : string.Create(CultureInfo.InvariantCulture,
              $"{WorkspaceRoot} \u00B7 {live.ProviderName} \u00B7 session {live.RootId}");
    }
  }

  /// <summary>Whether the tab holds no agent at all, so it renders its empty-state prompt
  ///     rather than a blank pane.</summary>
  public bool IsEmpty
  {
    get
    {
      lock (_cellsGate)
      {
        return Cells.Count == 0;
      }
    }
  }

  /// <summary>Whether this tab shows the workspace identified by <paramref name="workspaceId"/>.
  ///     The identity is compared when the tab carries one, and the directory path when it does
  ///     not, so a tab is still resolvable in a host that wired no workspace context.</summary>
  /// <param name="workspaceId">The workspace an agent's own record names.</param>
  public bool OwnsWorkspace(string workspaceId)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
    return WorkspaceId is { } id
        ? string.Equals(id, workspaceId, StringComparison.Ordinal)
        : WorkspaceIdentity.PathsEqual(WorkspaceRoot, workspaceId);
  }

  /// <summary>Whether this tab is the shell's selected one. Drives the keep-alive
  ///     content panel's visibility: every open tab's view stays built; only the
  ///     selected one is visible. Set by MainViewModel.OnSelectedTabChanged.</summary>
  [ObservableProperty]
  public partial bool IsSelected { get; set; }

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
    lock (_cellsGate)
    {
      Cells.Add(cell);
    }

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
    lock (_cellsGate)
    {
      Cells.Add(cell);
    }

    cell.StartFollowing();
    return cell;
  }

  /// <summary>Closes one card when the user clicks its x: the shell stops the agent and
  ///     releases its host, then removes the card. Null when the grid has no shell attached, so
  ///     a grid hosted on its own renders no close control.</summary>
  public Func<AgentCellViewModel, Task>? CloseCellRequested { get; private set; }

  /// <summary>Sets the close request for this grid. Calling again replaces it, so a grid holds
  ///     at most one and a click closes at most one agent.</summary>
  /// <param name="request">Closes one card of this grid.</param>
  public void AttachCloseCell(Func<AgentCellViewModel, Task> request)
  {
    ArgumentNullException.ThrowIfNull(request);
    CloseCellRequested = request;
  }

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
    OnPropertyChanged(nameof(Tooltip));
  }

  /// <summary>Finds the cell showing <paramref name="agentId"/>, or null when this tab shows
  ///     none. The one lookup every caller uses, because a cell's identity is its agent rather
  ///     than its position (D7).</summary>
  public AgentCellViewModel? FindCell(AgentId agentId)
    => Snapshot().FirstOrDefault(c => c.RootId == agentId);

  /// <summary>Removes a cell the user closed (D7), whatever state its agent is in.
  ///     <para>A LIVE RUN IS STOPPED FIRST: detaching it without stopping would leave the agent
  ///     working with no surface to see it or steer it.</para>
  ///     <para>THE LAST CELL IS ORDINARY (issue #339): removing it leaves a tab with zero
  ///     agents, which still knows its workspace and still offers its '+'.</para>
  ///     <para>Returns false when the cell is not this tab's, so the caller can say why.
  ///     </para></summary>
  public bool DismissCell(AgentCellViewModel cell)
  {
    ArgumentNullException.ThrowIfNull(cell);
    lock (_cellsGate)
    {
      if (!Cells.Contains(cell))
      {
        return false;
      }
    }

    if (cell.ViewModel.IsBusy)
    {
      cell.ViewModel.RequestStop();
    }

    cell.PropertyChanged -= OnCellPropertyChanged;
    cell.Detach();
    lock (_cellsGate)
    {
      return Cells.Remove(cell);
    }
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
  public bool IsFullyFinished
  {
    get
    {
      AgentCellViewModel[] cells = Snapshot();
      return cells.Length == 0 || cells.All(c => c.IsFinished);
    }
  }

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
  {
    OnPropertyChanged(nameof(IsFullyFinished));
    OnPropertyChanged(nameof(IsEmpty));
    OnPropertyChanged(nameof(ViewModel));
    OnPropertyChanged(nameof(Subtitle));
    OnPropertyChanged(nameof(Tooltip));
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

  /// <summary>Attaches the statusline branch watcher over the tab's workspace.
  ///     Calling again disposes the prior watcher first.</summary>
  public void AttachBranchWatcher(IGitQueryAccess? git)
  {
    _branchWatcher?.Dispose();
    _branchWatcher = git is null || LiveViewModel is not { } vm
        ? null
        : new GitBranchWatcher(WorkspaceRoot, git, vm.Status);
  }

  /// <summary>Disposes the branch watcher (tab close). Safe when none was attached.</summary>
  public void DetachBranchWatcher() => _branchWatcher?.Dispose();
}
