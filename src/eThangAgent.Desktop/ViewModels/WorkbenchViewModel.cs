using CommunityToolkit.Mvvm.ComponentModel;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>The workbench's view identities (design D8: six icons for nine views, grouped).
///     <see cref="Plans"/>, <see cref="Chains"/>, <see cref="Git"/>, <see cref="GitHub"/>, and
///     <see cref="Progress"/> are implemented; the rest are named here so the strip's shape is
///     settled and a later view is a case, not a redesign.
///     <para><see cref="Chains"/> sits BESIDE <see cref="Plans"/> rather than inside it (issue
///     #312 D5): a plan is the design and a chain is its execution, so they are different
///     artifacts with different lifetimes and each gets its own entry.</para></summary>
internal enum WorkbenchView
{
  Plans,
  Chains,
  Git,
  Files,
  Progress,
  GitHub,
  Database,
}

/// <summary>One icon in the workbench strip.</summary>
internal sealed partial class WorkbenchIconViewModel(WorkbenchView view, string glyph, string tooltip)
    : ObservableObject
{
  public WorkbenchView View { get; } = view;

  public string Glyph { get; } = glyph;

  public string Tooltip { get; } = tooltip;

  /// <summary>Whether this icon is the active one. Set by the shell, never by the icon.</summary>
  [ObservableProperty]
  public partial bool IsActive { get; set; }

  /// <summary>False for a view this slice has not built yet: the icon renders dimmed and
  ///     does not switch. An icon that silently does nothing is worse than one that says
  ///     it is not ready.</summary>
  public bool IsAvailable { get; init; }
}

/// <summary>The right-hand workbench (design D1/D5, issue #8): a PERMANENT column beside
///     the chat with its own icon strip, showing one view at a time.
///     <para>D5 retired the auto-open-on-subagents mechanism: the workbench does not
///     appear and disappear on content, because a pane that vanishes while you are
///     reading it is worse than one you collapse yourself. The chat keeps its full width
///     whenever the workbench is collapsed, and collapsing is the user's choice only.</para>
///     <para>Per-tab (D3): each tab owns one of these, so each tab remembers its own view.</para></summary>
internal sealed partial class WorkbenchViewModel : ObservableObject, IDisposable
{
  public WorkbenchViewModel(
      PlansViewViewModel plans,
      GitViewViewModel git,
      SubagentPanelViewModel subagents,
      PullRequestPaneViewModel pullRequests,
      ChainsViewViewModel? chains = null)
  {
    Plans = plans ?? throw new ArgumentNullException(nameof(plans));
    Git = git ?? throw new ArgumentNullException(nameof(git));
    Subagents = subagents ?? throw new ArgumentNullException(nameof(subagents));
    PullRequests = pullRequests ?? throw new ArgumentNullException(nameof(pullRequests));
    Chains = chains ?? ChainsViewViewModel.CreateUnavailable();
    Icons =
    [
      new WorkbenchIconViewModel(WorkbenchView.Plans, "\U0001F4CB", "Plans") { IsAvailable = true },
      // The CHAIN view (issue #312) is its own entry beside Plans (D5): the plan is the design,
      // the chain is its execution, and the badge count on this icon is what makes a chain
      // waiting for a human visible from the strip without opening the pane.
      new WorkbenchIconViewModel(WorkbenchView.Chains, "\U0001F517", "Chains") { IsAvailable = true },
      new WorkbenchIconViewModel(WorkbenchView.Git, "\U0001F33F", "Git status and diff") { IsAvailable = true },
      new WorkbenchIconViewModel(WorkbenchView.Files, "\U0001F5C2", "File tree (not yet built)"),
      // The Progress view IS the subagent accordion this workbench generalizes (D5):
      // live agent progress is exactly what it renders, so retiring the auto-open
      // mechanism costs no functionality.
      new WorkbenchIconViewModel(WorkbenchView.Progress, "\u26A1", "Live agent progress") { IsAvailable = true },
      new WorkbenchIconViewModel(WorkbenchView.GitHub, "\U0001F50D", "Pull requests and CI") { IsAvailable = true },
      new WorkbenchIconViewModel(WorkbenchView.Database, "\U0001F6E2", "App database (not yet built)"),
    ];
    Select(WorkbenchView.Plans);
  }

  public PlansViewViewModel Plans { get; }

  /// <summary>The chains view (issue #312): every chain in the workspace with its status, its
  ///     current step, and each step's outcome. Its OWN rail entry beside Plans (D5) - a plan is
  ///     the design and a chain is its execution, so a user looking for one does not scan past
  ///     the other.
  ///     <para>Its <see cref="ChainsViewViewModel.ParkedCountChanged"/> is what the status line's
  ///     badge mirrors (D6): the rail may be closed, and a chain waiting for a human is the one
  ///     thing in this application that needs attention.</para></summary>
  public ChainsViewViewModel Chains { get; }

  /// <summary>The git status and diff view (issue #9): branch, working-tree changes, and
  ///     the selected path's diff, refreshed by a watch over the workspace root.</summary>
  public GitViewViewModel Git { get; }

  /// <summary>The live agent progress view (design D5): the subagent accordion, moved
  ///     from its own auto-opening column into a workbench view.</summary>
  public SubagentPanelViewModel Subagents { get; }

  /// <summary>The GitHub view (issue #10): the repository's open pull requests, one PR's
  ///     detail, and a create form. Slower realtime than the other views by design - every
  ///     read spawns the gh CLI, so it polls on a long interval and only while visible.</summary>
  public PullRequestPaneViewModel PullRequests { get; }

  public IReadOnlyList<WorkbenchIconViewModel> Icons { get; }

  /// <summary>Whether the workbench column is showing. Defaults to open so the feature is
  ///     discoverable on first run; the user's collapse is what closes it (D5).</summary>
  [ObservableProperty]
  public partial bool IsOpen { get; set; } = true;

  /// <summary>The currently selected view.</summary>
  [ObservableProperty]
  public partial WorkbenchView SelectedView { get; private set; }

  /// <summary>True while the Plans view is the selected one, so the view can swap content
  ///     without a converter per view.</summary>
  public bool IsPlansSelected => SelectedView == WorkbenchView.Plans;

  /// <summary>True while the Chains view is the selected one (issue #312).</summary>
  public bool IsChainsSelected => SelectedView == WorkbenchView.Chains;

  /// <summary>True while the live-progress view is the selected one.</summary>
  public bool IsProgressSelected => SelectedView == WorkbenchView.Progress;

  /// <summary>True while the git view is the selected one.</summary>
  public bool IsGitSelected => SelectedView == WorkbenchView.Git;

  /// <summary>True while the GitHub view is the selected one.</summary>
  public bool IsGitHubSelected => SelectedView == WorkbenchView.GitHub;

  /// <summary>Selects a view. Unavailable views are ignored (their icons say so).
  ///     Selecting a view starts its realtime sources; a view nobody is looking at has
  ///     no reason to watch or poll.
  ///     <para>Also opens the workbench: picking an icon while collapsed is a request to
  ///     see it.</para></summary>
  public void Select(WorkbenchView view)
  {
    WorkbenchIconViewModel? icon = Icons.FirstOrDefault(i => i.View == view);
    if (icon is null || !icon.IsAvailable)
    {
      return;
    }

    SelectedView = view;
    foreach (WorkbenchIconViewModel candidate in Icons)
    {
      candidate.IsActive = candidate.View == view;
    }

    OnPropertyChanged(nameof(IsPlansSelected));
    OnPropertyChanged(nameof(IsChainsSelected));
    OnPropertyChanged(nameof(IsProgressSelected));
    OnPropertyChanged(nameof(IsGitSelected));
    OnPropertyChanged(nameof(IsGitHubSelected));
    IsOpen = true;

    // Only the SELECTED view runs its realtime source. The GitHub view spawns a subprocess
    // per tick, so leaving it started while another view is on screen would cost real work
    // for nothing - the pane stops the moment it stops being looked at.
    if (view != WorkbenchView.GitHub)
    {
      PullRequests.Stop();
    }

    if (view == WorkbenchView.Plans)
    {
      Plans.Start();
      _ = Plans.RefreshAsync(CancellationToken.None);
    }

    if (view == WorkbenchView.Chains)
    {
      Chains.Start();
      _ = Chains.RefreshAsync(CancellationToken.None);
    }

    if (view == WorkbenchView.Git)
    {
      // Start() runs the first read; the watcher then keeps it current.
      Git.Start();
    }

    if (view == WorkbenchView.GitHub)
    {
      // Start() runs the first read AND starts the slow poll; it stops when the view is
      // deselected (above), when the workbench collapses, or when the tab closes.
      PullRequests.Start();
    }
  }

  /// <summary>A collapsed workbench shows no view, so no view keeps running: the GitHub
  ///     poll is the one realtime source with a cost outside the process.</summary>
  partial void OnIsOpenChanged(bool value)
  {
    if (!value)
    {
      PullRequests.Stop();
    }
  }

  public void Dispose()
  {
    Plans.Dispose();
    Chains.Dispose();
    Git.Dispose();
    PullRequests.Dispose();
  }
}
