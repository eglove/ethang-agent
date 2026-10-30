using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Views;

/// <summary>A workspace tab (issue #307 D1): the agent grid plus the workbench rail.
///     <para>The grid's geometry comes from <see cref="AgentGridLayout"/> - a pure function -
///     and is applied here on every layout pass, so the view never re-derives it and the
///     arithmetic is testable without a window. The rail is the TAB's (D6), which is why this
///     view owns the splitter, the strip, and the collapse/reopen controls rather than any
///     one cell.</para>
///     <para>Each cell hosts an unchanged <see cref="AgentView"/>, so every agent renders
///     live (D3) and carries its own chat input (D4) with no per-cell special casing.</para></summary>
internal partial class WorkspaceTabView : UserControl
{
  private AgentTabViewModel? Vm => DataContext as AgentTabViewModel;

  /// <summary>The tab whose workbench is currently observed. A view instance may see several
  ///     DataContexts, so the previous tab is always unsubscribed first.</summary>
  private AgentTabViewModel? _wiredTab;

  /// <summary>Workbench width while open. Wide enough for the icon strip plus a readable
  ///     content pane; the splitter adjusts it from here.</summary>
  private const double WorkbenchDefaultWidth = 354;

  /// <summary>The width to restore when the workbench reopens. Collapsing sets the column to
  ///     zero, so the column itself cannot be the memory: reading it back would restore 0 and
  ///     the workbench would never reappear. The splitter's last width is captured here.
  ///     <para>Captured per tab, so two tabs remember their own rail width.</para></summary>
  private double _workbenchWidth = WorkbenchDefaultWidth;

  public WorkspaceTabView()
  {
    InitializeComponent();
    DataContextChanged += OnDataContextChanged;
  }

  /// <summary>Re-wires the rail subscription to the incoming tab, unsubscribing the previous
  ///     one first (the same leak fix the cell view carries).</summary>
  private void OnDataContextChanged(object? sender, EventArgs e)
  {
    _wiredTab?.Workbench.PropertyChanged -= OnWorkbenchPropertyChanged;
    _wiredTab = null;

    _workbenchWidth = WorkbenchDefaultWidth;
    if (Vm is not { } vm)
    {
      return;
    }

    _wiredTab = vm;
    vm.Workbench.PropertyChanged += OnWorkbenchPropertyChanged;
    ApplyWorkbenchLayout(vm.Workbench.IsOpen);
  }

  /// <summary>Sizes the workbench column from the workbench's OPEN state (issue #8, design
  ///     D5). The column is a pixel width while open - a GridSplitter resizes a column
  ///     definition, so an Auto column would leave the splitter inert - and zero while
  ///     collapsed, so the grid reclaims the full width.</summary>
  private void OnWorkbenchPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
  {
    if (e.PropertyName == nameof(WorkbenchViewModel.IsOpen) && sender is WorkbenchViewModel workbench)
    {
      ApplyWorkbenchLayout(workbench.IsOpen);
    }
  }

  private void ApplyWorkbenchLayout(bool isOpen)
  {
    Grid? root = this.FindControl<Grid>("TabRoot");
    if (root is null || root.ColumnDefinitions.Count < 3)
    {
      return;
    }

    // Only a LIVE (non-zero) width is worth remembering: it is what the splitter left.
    GridLength current = root.ColumnDefinitions[2].Width;
    if (current.IsAbsolute && current.Value > 0)
    {
      _workbenchWidth = current.Value;
    }

    root.ColumnDefinitions[2].Width = isOpen ? new GridLength(_workbenchWidth) : new GridLength(0);
  }

  /// <summary>Collapses the workbench (design D5). The workbench never hides itself on
  ///     content, so this deliberate click is the only thing that closes it; the grid then
  ///     reclaims the full width.</summary>
  private void OnWorkbenchCollapseClick(object? sender, RoutedEventArgs e)
  {
    if (Vm is { } vm)
    {
      vm.Workbench.IsOpen = false;
    }
  }

  /// <summary>Reopens the workbench (design D5): the complement of
  ///     <see cref="OnWorkbenchCollapseClick"/>, so collapsing is reversible.</summary>
  private void OnWorkbenchReopenClick(object? sender, RoutedEventArgs e)
  {
    if (Vm is { } vm)
    {
      vm.Workbench.IsOpen = true;
    }
  }

  /// <summary>Workbench icon click (issue #8): selects that view. The view-model ignores a
  ///     view it has not built, so an unavailable icon is inert without the view checking.</summary>
  private void OnWorkbenchIconClick(object? sender, RoutedEventArgs e)
  {
    if (sender is Button { Tag: WorkbenchView view } && Vm is { } vm)
    {
      vm.Workbench.Select(view);
    }
  }

  /// <summary>Git view change-row click (issue #9): renders that path's diff. The row's Tag
  ///     carries the path a diff can be asked for (a rename's NEW path), so the view never has
  ///     to parse porcelain text.
  ///     <para>Fire-and-forget by design: the read is a git invocation, and blocking the click
  ///     handler on it would freeze the shell for the query's duration.</para></summary>
  private void OnGitChangeClick(object? sender, RoutedEventArgs e)
  {
    if (sender is Button { Tag: string path } && Vm is { } vm)
    {
      _ = vm.Workbench.Git.SelectAsync(path, CancellationToken.None);
    }
  }

  /// <summary>GitHub view PR-row click (issue #10): loads that PR's body into the detail
  ///     area. The row's Tag carries the NUMBER, so the view never parses the display line.
  ///     <para>Fire-and-forget by design: the read is a gh subprocess, and blocking the click
  ///     handler on it would freeze the shell for the call's duration.</para></summary>
  private void OnPullRequestClick(object? sender, RoutedEventArgs e)
  {
    if (sender is Button { Tag: int number } && Vm is { } vm)
    {
      _ = vm.Workbench.PullRequests.SelectAsync(number, CancellationToken.None);
    }
  }

  /// <summary>Opens the create form seeded with the branches already on screen: the
  ///     checked-out branch as HEAD and its base as BASE, so the ordinary case is a title and
  ///     a click. Both come from the statusline's own branch facts rather than a new read.</summary>
  private void OnNewPullRequestClick(object? sender, RoutedEventArgs e)
  {
    if (Vm is not { } vm)
    {
      return;
    }

    vm.Workbench.PullRequests.BeginCreate(vm.ViewModel.Status.Branch, vm.Workbench.Git.SuggestedBaseBranch);
  }

  /// <summary>Sends the create request. Fire-and-forget for the same reason as the PR row:
  ///     the CLI runs a network call, and the form's error slot is what reports a refusal.</summary>
  private void OnSubmitPullRequestClick(object? sender, RoutedEventArgs e)
  {
    if (Vm is { } vm)
    {
      _ = vm.Workbench.PullRequests.SubmitCreateAsync(CancellationToken.None);
    }
  }

  /// <summary>Closes the create form without sending anything.</summary>
  private void OnCancelPullRequestClick(object? sender, RoutedEventArgs e) =>
      Vm?.Workbench.PullRequests.CancelCreate();

  /// <summary>Expands or collapses a chain card (issue #312 D7). The choice is recorded on the
  ///     VIEW, not on the card, because the card is REPLACED on every refresh: a toggle held only
  ///     on the old card would snap shut the moment anything moved.</summary>
  private void OnChainExpandClick(object? sender, RoutedEventArgs e)
  {
    if (sender is Button { Tag: ChainCardViewModel card } && Vm is { } vm)
    {
      card.IsExpanded = !card.IsExpanded;
      vm.Workbench.Chains.RememberExpansion(card);
    }
  }

  /// <summary>Opens a chain step's session from the rail (issue #312 D8): the ONE action, and it
  ///     is the Sessions menu's own operation.
  ///     <para>Fire-and-forget by design: the resume builds a container and launches a host, and
  ///     blocking the click handler on it would freeze the shell for its duration.</para>
  ///     <para>The task is deliberately unobserved: the view-model records its own refusal in
  ///     <c>ActionMessage</c>, and the shell reports a resume failure as a transcript notice - so
  ///     there is no outcome here for a caller to act on.</para></summary>
  private void OnChainOpenSessionClick(object? sender, RoutedEventArgs e)
  {
    if (sender is Button { Tag: ChainCardViewModel card } && Vm is { } vm)
    {
      _ = vm.Workbench.Chains.OpenSessionAsync(card, CancellationToken.None);
    }
  }

  /// <summary>Dismisses a FINISHED cell (D7). The card is removed only when the tab agrees -
  ///     a running cell or the primary cell is refused - so a click can never orphan a live
  ///     session or leave a tab whose primary session names nothing.</summary>
  private void OnDismissCellClick(object? sender, RoutedEventArgs e)
  {
    if (sender is Button { Tag: AgentCellViewModel cell } && Vm is { } vm)
    {
      _ = vm.DismissCell(cell);
    }
  }

  /// <summary>Attaches per-row sticky auto-scroll wiring when a subagent row's Expander
  ///     materializes (the Progress view's accordion). Re-attachment for the same row replaces
  ///     the old wiring - a recycled container gets a fresh viewer, so the stale subscription
  ///     must go first.
  ///     <para>FindControl cannot be used here: this handler fires from DataContext inheritance
  ///     DURING ContentPresenter.ApplyTemplate (mid layout pass), before the template's name
  ///     scope registers - FindControl threw 'Could not find parent name scope' and killed the
  ///     dispatcher (the 2026-10-18 spawn crash). The visual subtree is searched instead; when
  ///     the Expander's content has not materialized yet the wiring attaches on the NEXT
  ///     DataContextChanged, so a miss is a deferred attach, never a lost one.</para></summary>
  private void OnRowDataContextChanged(object? sender, EventArgs e)
  {
    if (sender is not Expander { DataContext: SubagentRowViewModel row } expander)
    {
      return;
    }

    if (_rowWirings.TryGetValue(row, out RowScrollWiring? existing))
    {
      existing.Dispose();
      _ = _rowWirings.Remove(row);
    }

    ScrollViewer? viewer = expander.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
    if (viewer is null)
    {
      return;
    }

    _rowWirings.Add(row, new RowScrollWiring(row, viewer));
  }

  /// <summary>Live per-row wirings, keyed by the row VM whose DataContext materialized them.</summary>
  private readonly Dictionary<SubagentRowViewModel, RowScrollWiring> _rowWirings = [];

  protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
  {
    base.OnDetachedFromVisualTree(e);
    ReleaseRowWirings(); // recycled containers outlive this view instance
  }

  /// <summary>Releases every per-row wiring: the panel's rows are about to change identity
  ///     (tab rebuild, VM re-wire) and stale subscriptions must not outlive their row.</summary>
  private void ReleaseRowWirings()
  {
    foreach (RowScrollWiring wiring in _rowWirings.Values)
    {
      wiring.Dispose();
    }

    _rowWirings.Clear();
  }
}
