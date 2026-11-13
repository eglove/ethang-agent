using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

/// <summary>The chat must never scroll sideways. Only a code block does.
///     <para>The status bar is a horizontal row of readout segments and it was a
///     <c>StackPanel</c>, which reports the SUM of its children as its desired width and never
///     constrains them. A long model id therefore made the bar desire more than the column had;
///     because the bar stretches to fill the chat's dock panel, that desire travelled up through
///     the cell into the grid panel, the cell demanded more width than the viewport, and the
///     tab's <c>Auto</c> scroll viewer turned it into a full-width horizontal scrollbar across
///     the bottom of the chat - the bar the user must never see.</para>
///     <para>The invariant: however wide the status content gets, the transcript never demands
///     more width than it is given. Markdown then wraps in the column instead of running under
///     the workbench rail.</para></summary>
[Trait("Category", "Process")]
public class ChatNeverScrollsSidewaysTests
{
  private const string LongModel = "deepseek/deepseek-v4.1-flash-1017-ultra-long-model-identifier";

  /// <summary>A wide status line must not widen the chat. Measured on the tab's own scroll
  ///     viewer, because that is what actually grows the scrollbar the user sees.</summary>
  [AvaloniaFact]
  public void Wide_Status_Line_Does_Not_Make_The_Chat_Scroll_Sideways()
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();
    Window window = new() { Width = 1152, Height = 760, Content = new WorkspaceTabView { DataContext = tab } };
    window.Show();
    Dispatcher.UIThread.RunJobs();

    AgentSessionViewModel session = tab.Cells[0].ViewModel;
    session.Status.ModelId = LongModel;
    session.Status.Branch = "feature/an-unusually-long-branch-name-for-this-workspace";
    session.Status.Effort = "Model default";
    session.Status.Phase = TurnPhase.Streaming;
    Dispatcher.UIThread.RunJobs();
    Dispatcher.UIThread.RunJobs();
    // The status content above is written AFTER the window was shown, so the row is still holding
    // the width it was arranged at for the short values it opened with. A resize is what makes the
    // shell re-run the layout pass; restore the width afterwards so the measurement below is the
    // one this test is about.
    double width = window.Width;
    window.Width = width + 1;
    Dispatcher.UIThread.RunJobs();
    window.Width = width;
    Dispatcher.UIThread.RunJobs();
    Dispatcher.UIThread.RunJobs();

    // The status row is the widest unbreakable thing in the chat, so it is what used to push the
    // column past its own width. It must now fit the width it is given.
    Control status = window.GetVisualDescendants().OfType<Control>()
        .First(s => s.Name == "StatusInfo");
    Assert.True(status.DesiredSize.Width <= status.Bounds.Width + 1,
        $"the status row must fit the width it is given, not desire more: " +
        $"desired {status.DesiredSize.Width:F0} vs bounds {status.Bounds.Width:F0}");

    // Above the documented 480 px readability floor there is nothing to scroll: the chat fills
    // its column exactly. (Below that floor the grid scrolls by design - AgentGridLayout D8.)
    ScrollViewer cells = window.GetVisualDescendants().OfType<ScrollViewer>()
        .First(s => s.Name == "CellScroll");
    Assert.True(cells.Viewport.Width > AgentGridLayout.MinCellWidth,
        $"precondition: the viewport must be above the 480 px floor, was {cells.Viewport.Width:F0}");
    Assert.True(cells.Extent.Width <= cells.Viewport.Width + 1,
        $"the chat must not overflow sideways: extent {cells.Extent.Width:F0} vs viewport {cells.Viewport.Width:F0}");
    window.Close();
  }

  /// <summary>Markdown keeps wrapping inside the column, so no transcript block ever wants
  ///     more width than the transcript has. This is the half that keeps text out from under the
  ///     workbench rail.</summary>
  [AvaloniaFact]
  public void Markdown_Never_Demands_More_Width_Than_The_Transcript_Has()
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();
    Window window = new() { Width = 1152, Height = 760, Content = new WorkspaceTabView { DataContext = tab } };
    window.Show();
    Dispatcher.UIThread.RunJobs();

    AgentSessionViewModel session = tab.Cells[0].ViewModel;
    session.Status.ModelId = LongModel;
    session.Status.Branch = "feature/an-unusually-long-branch-name-for-this-workspace";
    session.Transcript.AppendAssistantDelta(
        "A paragraph long enough to need wrapping, repeated so the line is wide. " +
        "A paragraph long enough to need wrapping, repeated so the line is wide. " +
        "A paragraph long enough to need wrapping, repeated so the line is wide.");
    Dispatcher.UIThread.RunJobs();
    Dispatcher.UIThread.RunJobs();

    ScrollViewer transcript = window.GetVisualDescendants().OfType<ScrollViewer>()
        .First(s => s.Name == "TranscriptScroll");
    Assert.True(transcript.Extent.Width <= transcript.Viewport.Width + 1,
        $"the transcript must not overflow sideways: extent {transcript.Extent.Width:F0} vs viewport {transcript.Viewport.Width:F0}");
    window.Close();
  }

  /// <summary>An exec code block is the ONE place that may scroll sideways, so the fix must
  ///     not remove it while removing the chat's scrollbar.</summary>
  [AvaloniaFact]
  public void Exec_Code_Block_Still_Scrolls_Sideways()
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();
    Window window = new() { Width = 1000, Height = 700, Content = new WorkspaceTabView { DataContext = tab } };
    window.Show();
    Dispatcher.UIThread.RunJobs();

    AgentSessionViewModel session = tab.Cells[0].ViewModel;
    session.Transcript.AddToolCall("exec", /*lang=json,strict*/ "{\"timeoutSeconds\":60,\"title\":\"probe\",\"program\":\"var r = Shell(\\\"dotnet\\\", \\\"test\\\", \\\"tests/eThangAgent.Desktop.Tests/eThangAgent.Desktop.Tests.csproj\\\", \\\"--filter-class\\\", \\\"eThangAgent.Desktop.Tests.NewAgentGateSectionTests\\\"); return ok;\"}");
    Dispatcher.UIThread.RunJobs();
    Dispatcher.UIThread.RunJobs();

    ScrollViewer codeScroller = window.GetVisualDescendants().OfType<ScrollViewer>()
        .First(s => s.HorizontalScrollBarVisibility == Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
    Assert.NotNull(codeScroller);
    window.Close();
  }
}
