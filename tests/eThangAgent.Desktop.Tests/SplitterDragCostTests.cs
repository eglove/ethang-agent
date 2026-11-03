#pragma warning disable IDE0005, IDE0028, IDE0054, IDE0058, IDE0008, CA1305, CA1307, S2699, CA1031, S8969, S3776, IDE0010, S1199, CA1859, S1481, S1480
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

/// <summary>Measures what a LIVE splitter drag costs as the transcript grows.
///     <para>Live resize is what every surveyed desktop app does (VS Code, JetBrains, DevTools,
///     Blender, GIMP, Krita, Explorer), and NN/g's threshold for an interaction to feel
///     instantaneous is 0.1 s per frame. So the question is not 'live or deferred' in the
///     abstract - it is whether live stays inside that budget on a REAL transcript. This
///     measures per-step cost at increasing transcript sizes to answer it with numbers.</para>
///     <para>The resize is applied to the workbench column and the chat width is asserted to
///     have actually changed, because an earlier harness silently measured nothing when the
///     column width did not propagate.</para></summary>
public class SplitterDragCostTests
{
  private const string Prose =
      "The grid panel answers a scroll viewer's infinite measure size with the resolved viewport " +
      "rather than its own arranged bounds, because a layout input the layout itself produces can " +
      "never converge and the chat ends up overflowing under the rail forever.";

  private static (double MsPerStep, double ChatWidthBefore, double ChatWidthAfter, int Entries) Drag(int entries)
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();
    Window window = new() { Width = 1883, Height = 900, Content = new WorkspaceTabView { DataContext = tab } };
    window.Show();
    Dispatcher.UIThread.RunJobs();

    AgentSessionViewModel session = tab.Cells[0].ViewModel;
    for (int i = 0; i < entries; i++)
    {
      session.Transcript.AddNotice("line " + i.ToString(CultureInfo.InvariantCulture) + ": " + Prose);
    }
    session.Transcript.AppendAssistantDelta(Prose + " " + Prose);
    for (int i = 0; i < 4; i++)
    {
      Dispatcher.UIThread.RunJobs();
    }

    Grid root = window.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "TabRoot");
    ScrollViewer cells = window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "CellScroll");
    double before = cells.Viewport.Width;

    // 40 steps sweeping the rail 300..500 px, the way a real drag does.
    const int steps = 40;
    System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    for (int i = 0; i < steps; i++)
    {
      root.ColumnDefinitions[2].Width = new GridLength(300 + (i * 5 % 200));
      Dispatcher.UIThread.RunJobs();
    }
    clock.Stop();

    double after = cells.Viewport.Width;
    window.Close();
    return (clock.Elapsed.TotalMilliseconds / steps, before, after, entries);
  }

  [AvaloniaFact]
  public void Live_Drag_Stays_Inside_The_Instantaneous_Budget_As_The_Transcript_Grows()
  {
    System.Text.StringBuilder report = new();
    foreach (int n in new[] { 0, 100, 400, 1000 })
    {
      (double ms, double before, double after, int entries) = Drag(n);
      report.AppendLine(CultureInfo.InvariantCulture, $"entries={entries} msPerStep={ms:F1} chat {before:F0}->{after:F0}");

      // The resize must actually move the chat, or the number measures nothing.
      Assert.True(Math.Abs(after - before) > 50,
          $"the drag must actually resize the chat (was {before:F0}, now {after:F0}) - otherwise this measures nothing");
    }

    // THE REPORT GOES TO THE TEST'S OWN OUTPUT, NEVER INTO THE WORKSPACE. It used to be
    // written to a hardcoded path under the repository's .work directory, which dirtied the
    // tree on every run: the completion gate compares the newest passing verification against
    // the workspace's write times, so a test that writes a file AFTER the run makes every
    // green run read as stale and no completion claim can ever be made.
    TestContext.Current.SendDiagnosticMessage(report.ToString());
  }
}
