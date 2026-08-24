using CommunityToolkit.Mvvm.ComponentModel;
using eThangAgent.Composition;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>One open-agent tab in the shell: pairs the session container with its
///     view-model and supplies the tab-strip caption/subtitle. The header binds
///     Title; the content area binds ViewModel.</summary>
public sealed partial class AgentTabViewModel : ObservableObject
{
    public AgentSession Container { get; }

    public AgentSessionViewModel ViewModel { get; }

    public string Title { get; }

    public string Subtitle => ViewModel.WorkspaceRoot;

    public AgentTabViewModel(AgentSession session, AgentSessionViewModel sessionVm)
    {
        Container = session ?? throw new ArgumentNullException(nameof(session));
        ViewModel = sessionVm ?? throw new ArgumentNullException(nameof(sessionVm));
        Title = Path.GetFileName(
            session.WorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }
}