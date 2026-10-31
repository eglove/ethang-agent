using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

/// <summary>Opening the workbench must NARROW the chat, not lay it out underneath the rail.
///     <para>The grid panel answered a scroll viewer's infinite measure size with its OWN
///     arranged <c>Bounds</c>. That is circular - the panel's arranged size is both the measure
///     input and an output of arrange - so when the workbench opened and the scroll viewer
///     narrowed the panel's slot to 1523, the panel kept measuring against its previous 1883,
///     reported 1883, and left the scroll viewer with a permanent horizontal overflow. On
///     screen the chat text ran under the rail and a scrollbar sat across the bottom.</para>
///     <para>The fix reads the ancestor scroll viewer's VIEWPORT, which is decided above this
///     panel and therefore cannot feed on the panel's own output.</para></summary>
public class ChatShrinksForWorkbenchTests
{
  /// <summary>The transition is what broke: the workbench starts closed, the tab lays out at
  ///     full width, and only then does the rail open. A test that constructs the tab with the
  ///     rail already open never sees the stale width, which is why this one drives the open.</summary>
  [AvaloniaFact]
  public void Opening_The_Workbench_Narrows_The_Chat_Instead_Of_Overlapping_It()
  {
    (AgentTabViewModel tab, Window window) = Show();

    tab.Workbench.IsOpen = false;
    Dispatcher.UIThread.RunJobs();
    Dispatcher.UIThread.RunJobs();
    double closedWidth = CellScroll(window).Viewport.Width;

    tab.Workbench.IsOpen = true;
    Dispatcher.UIThread.RunJobs();
    Dispatcher.UIThread.RunJobs();

    ScrollViewer cells = CellScroll(window);
    Assert.True(cells.Viewport.Width < closedWidth - 100,
        $"opening the rail must narrow the chat: was {closedWidth:F0}, now {cells.Viewport.Width:F0}");
    Assert.True(cells.Extent.Width <= cells.Viewport.Width + 1,
        $"the chat must fit its narrowed column, not overflow under the rail: " +
        $"extent {cells.Extent.Width:F0} vs viewport {cells.Viewport.Width:F0}");
    window.Close();
  }

  /// <summary>The cell is arranged at the width it was measured at, after the transition.
  ///     A panel that keeps reporting its pre-shrink width is exactly the stale state this
  ///     pins: measure and arrange must agree once the layout settles.</summary>
  [AvaloniaFact]
  public void The_Cell_Is_Arranged_At_Its_Narrowed_Width_After_The_Rail_Opens()
  {
    (AgentTabViewModel tab, Window window) = Show();

    tab.Workbench.IsOpen = false;
    Dispatcher.UIThread.RunJobs();
    tab.Workbench.IsOpen = true;
    Dispatcher.UIThread.RunJobs();
    Dispatcher.UIThread.RunJobs();

    AgentGridPanel panel = window.GetVisualDescendants().OfType<AgentGridPanel>().First();
    ScrollViewer cells = CellScroll(window);
    Assert.Equal(cells.Viewport.Width, panel.Bounds.Width, 1.0);
    Assert.True(panel.LastPlan.CellWidth <= cells.Viewport.Width + 1,
        $"the plan must fit the viewport: cell {panel.LastPlan.CellWidth:F0} vs viewport {cells.Viewport.Width:F0}");
    window.Close();
  }

  /// <summary>The markdown column narrows with the chat, so prose wraps inside it rather than
  ///     running beneath the rail. This is the user-visible half.</summary>
  [AvaloniaFact]
  public void The_Transcript_Narrows_With_The_Chat_So_Prose_Wraps_Inside_It()
  {
    (AgentTabViewModel tab, Window window) = Show();
    tab.Cells[0].ViewModel.Transcript.AppendAssistantDelta(
        "A paragraph of prose long enough that it must wrap inside the column rather than run " +
        "under the rail once the workbench opens beside it.");
    Dispatcher.UIThread.RunJobs();

    tab.Workbench.IsOpen = false;
    Dispatcher.UIThread.RunJobs();
    Dispatcher.UIThread.RunJobs();
    double closed = TranscriptScroll(window).Viewport.Width;

    tab.Workbench.IsOpen = true;
    Dispatcher.UIThread.RunJobs();
    Dispatcher.UIThread.RunJobs();

    ScrollViewer transcript = TranscriptScroll(window);
    Assert.True(transcript.Viewport.Width < closed - 100,
        $"the transcript must narrow with the chat: was {closed:F0}, now {transcript.Viewport.Width:F0}");
    Assert.True(transcript.Extent.Width <= transcript.Viewport.Width + 1,
        $"prose must wrap inside the column, not extend it: extent {transcript.Extent.Width:F0} vs viewport {transcript.Viewport.Width:F0}");
    window.Close();
  }

  private static (AgentTabViewModel Tab, Window Window) Show()
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();
    Window window = new() { Width = 1883, Height = 782, Content = new WorkspaceTabView { DataContext = tab } };
    window.Show();
    Dispatcher.UIThread.RunJobs();
    return (tab, window);
  }

  private static ScrollViewer CellScroll(Window window) =>
      window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "CellScroll");

  private static ScrollViewer TranscriptScroll(Window window) =>
      window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "TranscriptScroll");
}
