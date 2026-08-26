using CommunityToolkit.Mvvm.ComponentModel;
using eThangAgent.Composition;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>One open-agent tab in the shell: pairs the session container with its
///     view-model and supplies the tab-strip caption/subtitle. The header binds
///     Title; the content area binds ViewModel.</summary>
internal sealed partial class AgentTabViewModel(AgentSession session, AgentSessionViewModel sessionVm) : ObservableObject
{
  public AgentSession Container { get; } = session ?? throw new ArgumentNullException(nameof(session));

  public AgentSessionViewModel ViewModel { get; } = sessionVm ?? throw new ArgumentNullException(nameof(sessionVm));

  public string Title { get; } = Path.GetFileName(
        session.WorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

  public string Subtitle => ViewModel.WorkspaceRoot;
}
