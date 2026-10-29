using CommunityToolkit.Mvvm.ComponentModel;
using eThangAgent.Composition;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>One agent cell inside a workspace tab (issue #307 D1): a session and its chat
///     surface, laid out beside its siblings.
///     <para>There is no orchestrator agent and therefore no privileged cell (D4/D9): every
///     cell owns its own chat input, so the user can steer any running agent or answer a
///     question from any waiting one. The cell carries no behaviour beyond that - the session
///     view-model owns the transcript, the turn loop, and the status bar.</para>
///     <para>A cell whose session closes PERSISTS as a finished card until the user dismisses
///     it (D7). It never reflows: in a grid the position carries meaning, and cell 3 was the
///     third thing you started.</para></summary>
internal sealed partial class AgentCellViewModel : ObservableObject
{
  public AgentCellViewModel(AgentSession session, AgentSessionViewModel sessionVm)
  {
    Container = session ?? throw new ArgumentNullException(nameof(session));
    ViewModel = sessionVm ?? throw new ArgumentNullException(nameof(sessionVm));
    // The outline follows the turn (D5): a stalled or waiting agent must be visible without
    // reading its transcript, so the cell watches the session's busy flag.
    ViewModel.PropertyChanged += OnSessionPropertyChanged;
  }

  public AgentSession Container { get; }

  public AgentSessionViewModel ViewModel { get; }

  /// <summary>True once this cell's session has closed. The cell stays in the grid as a
  ///     finished card (D7) - it is not removed and the remaining cells do not reflow.</summary>
  [ObservableProperty]
  public partial bool IsFinished { get; private set; }

  /// <summary>Whether this cell is the one the user is working in. Set by the tab, never by
  ///     the cell: focus is a property of the grid, not of the agent.</summary>
  [ObservableProperty]
  public partial bool IsFocused { get; internal set; }

  /// <summary>Whether this cell's agent is currently streaming a turn. Every cell renders
  ///     LIVE (D3), so this drives the outline rather than whether the cell renders at all
  ///     (D5).</summary>
  public bool IsStreaming => ViewModel.IsBusy;

  /// <summary>Whether the cell renders the distinct idle outline (D5). A finished card is
  ///     idle by definition, and a busy agent never is.</summary>
  public bool IsIdle => !IsFinished && !IsStreaming;

  /// <summary>The cell's caption: the workspace's directory name, which is what a user
  ///     recognises. Falls back to the session id when the root has no file name (a drive
  ///     root such as <c>C:\</c>).</summary>
  public string Title
  {
    get
    {
      string name = Path.GetFileName(
          Container.WorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
      return string.IsNullOrEmpty(name) ? Container.RootId.ToString()[..8] : name;
    }
  }

  /// <summary>Marks the cell finished. The session is already gone (or going); nothing here
  ///     disposes it, because the shell owns that teardown and must run it exactly once.
  ///     Calling again is a no-op, so a close path and a shutdown path cannot disagree.</summary>
  public void MarkFinished()
  {
    IsFinished = true;
    RaiseStateChanged();
  }

  /// <summary>Detaches the session subscription. Called when the tab drops the cell, so a
  ///     dismissed card cannot keep raising property changes for a cell nothing renders.</summary>
  public void Detach() => ViewModel.PropertyChanged -= OnSessionPropertyChanged;

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
