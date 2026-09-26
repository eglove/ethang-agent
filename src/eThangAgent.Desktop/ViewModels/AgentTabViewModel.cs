using CommunityToolkit.Mvvm.ComponentModel;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>One open-agent tab in the shell: pairs the session container with its
///     view-model and supplies the tab-strip caption/subtitle. The header binds
///     Title, Subtitle, and the unread-steering badge (W4.4) pushed by the
///     session's child-event stream. The content area binds ViewModel.</summary>
internal sealed partial class AgentTabViewModel(AgentSession session, AgentSessionViewModel sessionVm) : ObservableObject
{
  public AgentSession Container { get; } = session ?? throw new ArgumentNullException(nameof(session));

  public AgentSessionViewModel ViewModel { get; } = sessionVm ?? throw new ArgumentNullException(nameof(sessionVm));

  public string Title { get; } = Path.GetFileName(
        session.WorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

  public string Subtitle => ViewModel.WorkspaceRoot;

  /// <summary>Whether this tab is the shell's selected one. Drives the keep-alive
  ///     content panel's visibility: every open tab's AgentView stays built; only the
  ///     selected one is visible. Set by MainViewModel.OnSelectedTabChanged.</summary>
  [ObservableProperty]
  public partial bool IsSelected { get; set; }

  /// <summary>The tab's unread-steering badge, attached by the shell against the
  ///     session's child-event stream (W4.4). Null when no stream is wired
  ///     (headless/test hosts construct nothing).</summary>
  [ObservableProperty]
  public partial TabBadgeViewModel? Badge { get; private set; }

  /// <summary>Subscribes the badge to this session's child-event stream. Called once
  ///     by the shell after the tab is created; calling again detaches the prior badge.
  ///     The render callback posts through the UI Dispatcher - events arrive on stream
  ///     threads, transcript/badge mutation is UI-thread-only.</summary>
  public void AttachBadge(IAgentEvents? events)
  {
    Badge?.Dispose();
    Badge = TabBadgeViewModel.Attach(events, session.RootId, apply => count =>
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
