using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.Lsp.ACL;

namespace eThangAgent.Desktop.Tests;

/// <summary>The status bar's language-server segment, rendered: hidden until the daemon
///     announces something, and never wide enough to push the chat column sideways - the
///     bar is a DockPanel precisely because a row that desires more width than the chat has
///     turns into a horizontal scrollbar under the transcript.</summary>
public class LspStatusBarRenderingTests
{
  [AvaloniaFact]
  public void TheSegment_IsHidden_UntilTheDaemonAnnouncesSomething()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    Window window = new() { Content = new AgentView { DataContext = vm } };
    window.Show();
    AgentView view = (AgentView)window.Content;
    TextBlock server = view.GetControl<TextBlock>("ServerStatusText");

    Assert.False(server.IsVisible);

    vm.Status.SetServerStatus(LspServerInstallState.Downloading, string.Empty);

    Assert.True(server.IsVisible);
    Assert.Contains("downloading", server.Text, StringComparison.Ordinal);
  }

  [AvaloniaFact]
  public void TheSegment_SitsLeftOfTheSessionId()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    Window window = new() { Content = new AgentView { DataContext = vm } };
    window.Show();
    AgentView view = (AgentView)window.Content;
    DockPanel bar = view.GetControl<DockPanel>("StatusInfo");

    List<string> names = [.. bar.Children
        .OfType<Avalonia.StyledElement>()
        .Select(child => child.Name)
        .OfType<string>()
        .Where(name => name.Length > 0)];

    Assert.Contains("ServerStatusText", names);
    Assert.Equal(Dock.Left, DockPanel.GetDock(bar.Children[names.IndexOf("ServerStatusText")]));
  }

  [AvaloniaFact]
  public void ADownloadingServer_DoesNotMakeTheChatScrollSideways()
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();
    Window window = new() { Width = 1152, Height = 760, Content = new WorkspaceTabView { DataContext = tab } };
    window.Show();
    Dispatcher.UIThread.RunJobs();

    AgentSessionViewModel session = tab.Cells[0].ViewModel;
    session.Status.SetServerStatus(LspServerInstallState.Downloading, string.Empty);
    Dispatcher.UIThread.RunJobs();
    Dispatcher.UIThread.RunJobs();

    Control status = window.GetVisualDescendants().OfType<Control>()
        .First(s => s.Name == "StatusInfo");
    Assert.True(status.DesiredSize.Width <= status.Bounds.Width + 1,
        $"the status row must fit the width it is given: desired "
        + $"{status.DesiredSize.Width:F0} vs bounds {status.Bounds.Width:F0}");

    ScrollViewer cells = window.GetVisualDescendants().OfType<ScrollViewer>()
        .First(s => s.Name == "CellScroll");
    Assert.True(cells.Extent.Width <= cells.Viewport.Width + 1,
        $"the chat must not overflow sideways: extent {cells.Extent.Width:F0} "
        + $"vs viewport {cells.Viewport.Width:F0}");
    window.Close();
  }
}
