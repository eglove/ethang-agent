using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
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
///     the shell addresses 'the tab's session' and, with one cell, that is exactly right.</para></summary>
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
    AgentCellViewModel primary = new(session, sessionVm);
    primary.PropertyChanged += OnCellPropertyChanged;
    Cells = [primary];
    Cells.CollectionChanged += OnCellsChanged;
    Title = Path.GetFileName(
        session.WorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
  }

  /// <summary>The tab's primary session container. With one cell this is the tab's session;
  ///     with several it is the one the workspace was opened for.
  ///     <para>DELEGATED, not stored: a sequential chain advance REPLACES the primary cell in
  ///     place (D2), so a stored copy would name a session the tab no longer shows.</para></summary>
  public AgentSession Container => Cells[0].Container;

  /// <summary>The primary cell's view-model (see <see cref="Container"/>).</summary>
  public AgentSessionViewModel ViewModel => Cells[0].ViewModel;

  /// <summary>The workspace tab's agent cells, in the order they were started (D7: position
  ///     carries meaning, so a finished cell keeps its place rather than reflowing).</summary>
  public ObservableCollection<AgentCellViewModel> Cells { get; }

  /// <summary>The tab's workbench rail (D6): workspace-scoped, shared by every cell, and
  ///     unchanged by a cell's arrival or departure.</summary>
  public WorkbenchViewModel Workbench { get; }

  public string Title { get; }

  public string Subtitle => ViewModel.WorkspaceRoot;

  /// <summary>Whether this tab is the shell's selected one. Drives the keep-alive
  ///     content panel's visibility: every open tab's view stays built; only the
  ///     selected one is visible. Set by MainViewModel.OnSelectedTabChanged.</summary>
  [ObservableProperty]
  public partial bool IsSelected { get; set; }

  /// <summary>The tab's unread-steering badge, attached by the shell against the
  ///     session's child-event stream (W4.4). Null when no stream is wired
  ///     (headless/test hosts construct nothing).</summary>
  [ObservableProperty]
  public partial TabBadgeViewModel? Badge { get; private set; }

  /// <summary>Adds a cell for a NEW agent in this workspace (a parallel dispatch, D2).
  ///     The cell is appended, so the grid's existing cells keep their positions.
  ///     <para>A sequential chain advance does NOT come through here: it reuses the primary
  ///     cell (see MainViewModel.StartNewSessionAsync), because a chain is a sequence of
  ///     agents rather than several at once.</para></summary>
  public AgentCellViewModel AddCell(AgentSession session, AgentSessionViewModel sessionVm)
  {
    AgentCellViewModel cell = new(session, sessionVm);
    cell.PropertyChanged += OnCellPropertyChanged;
    Cells.Add(cell);
    return cell;
  }

  /// <summary>Replaces the PRIMARY cell's session IN PLACE (issue #234 + #307 D2): a
  ///     sequential advance (a chain step, `/new`, a compaction handoff) continues in the same
  ///     cell rather than adding one, because a chain is a SEQUENCE of agents and the grid's
  ///     positions carry meaning (D7).
  ///     <para>The tab itself survives, so its workbench rail and any sibling cells are
  ///     untouched - replacing the whole tab would rebuild a pane the user is reading and
  ///     discard agents that have nothing to do with the advance.</para>
  ///     <para>The caller owns the retired session's teardown; this only swaps the reference,
  ///     so a teardown fault can never leave the tab without a cell.</para></summary>
  public void ReplacePrimaryCell(AgentSession session, AgentSessionViewModel sessionVm)
  {
    ArgumentNullException.ThrowIfNull(session);
    ArgumentNullException.ThrowIfNull(sessionVm);
    AgentCellViewModel retired = Cells[0];
    retired.PropertyChanged -= OnCellPropertyChanged;
    retired.Detach();
    AgentCellViewModel replacement = new(session, sessionVm);
    replacement.PropertyChanged += OnCellPropertyChanged;
    Cells[0] = replacement;
    OnPropertyChanged(nameof(Container));
    OnPropertyChanged(nameof(ViewModel));
    OnPropertyChanged(nameof(Subtitle));
  }

  /// <summary>Removes a FINISHED cell the user dismissed (D7). A running cell is refused:
  ///     dismissing it would orphan a live session with no surface. The primary cell is
  ///     refused too - the tab's own identity is that session, and removing it would leave
  ///     a tab whose <see cref="Container"/> names nothing.
  ///     <para>Returns false when the cell was not dismissed, so the caller can say why.</para></summary>
  public bool DismissCell(AgentCellViewModel cell)
  {
    ArgumentNullException.ThrowIfNull(cell);
    if ((Cells.Count > 0 && ReferenceEquals(cell, Cells[0])) || !cell.IsFinished)
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
    AgentCellViewModel? cell = Cells.FirstOrDefault(c => c.Container.RootId == rootId);
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

  /// <summary>Subscribes the badge to this session's child-event stream. Called once
  ///     by the shell after the tab is created; calling again detaches the prior badge.
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
