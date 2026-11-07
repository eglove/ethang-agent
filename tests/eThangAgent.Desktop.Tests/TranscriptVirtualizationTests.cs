using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

/// <summary>The transcript VIRTUALIZES: only the entries on screen are realized.
///     <para>Without virtualization every entry is built and measured on every layout pass. A
///     live splitter drag re-measures the whole transcript per pointer move, so the cost scales
///     with history - measured at 2.4 ms per step on an empty chat, 137 ms at 100 entries,
///     523 ms at 400 and 1304 ms at 1000, against the 0.1 s budget an interaction needs to feel
///     instantaneous (Nielsen's response-time limit). Virtualizing bounds the per-frame work by
///     the VIEWPORT rather than by the conversation, which is what makes live resizing viable at
///     any transcript length.</para>
///     <para>These assert the CONTRACT, not an implementation: a bounded number of realized
///     entries out of a large collection, the tail reachable, and the entries that are realized
///     still being the right ones.</para></summary>
[Trait("Category", "Process")]
public class TranscriptVirtualizationTests
{
  private const int Many = 500;

  private static (AgentTabViewModel Tab, Window Window) Show(int entries)
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();
    Window window = new() { Width = 1200, Height = 700, Content = new WorkspaceTabView { DataContext = tab } };
    window.Show();
    Dispatcher.UIThread.RunJobs();

    AgentSessionViewModel session = tab.Cells[0].ViewModel;
    for (int i = 0; i < entries; i++)
    {
      session.Transcript.AddNotice("entry " + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + " filler text that occupies a line.");
    }

    for (int i = 0; i < 6; i++)
    {
      Dispatcher.UIThread.RunJobs();
    }

    return (tab, window);
  }

  /// <summary>The core contract: a 500-entry transcript must not realize 500 entries.</summary>
  [AvaloniaFact]
  public void A_Long_Transcript_Realizes_Only_What_Is_On_Screen()
  {
    (AgentTabViewModel tab, Window window) = Show(Many);

    int realized = RealizedEntryCount(window);
    // The collection holds every entry (plus the session's bootstrap line); the question is how
    // many of them were BUILT into views.
    Assert.True(tab.Cells[0].ViewModel.Transcript.Entries.Count >= Many,
        $"precondition: the whole transcript is in the collection, but it holds {tab.Cells[0].ViewModel.Transcript.Entries.Count}");
    Assert.True(realized > 0, "some entries must be realized - an empty view renders nothing");
    Assert.True(realized < Many / 2,
        $"a {Many}-entry transcript must not realize {realized} entries; only the visible window should be built");
    window.Close();
  }

  /// <summary>Virtualization must not break the thing the transcript is for: the tail is
  ///     reachable, and the newest entry is among the realized ones when scrolled to the bottom.</summary>
  [AvaloniaFact]
  public void The_Newest_Entry_Is_Reachable_At_The_Bottom()
  {
    (AgentTabViewModel _, Window window) = Show(Many);

    ScrollViewer transcript = window.GetVisualDescendants().OfType<ScrollViewer>()
        .First(s => s.Name == "TranscriptScroll");
    transcript.ScrollToEnd();
    for (int i = 0; i < 6; i++)
    {
      Dispatcher.UIThread.RunJobs();
    }

    Assert.Contains(RealizedText(window), t => t.Contains($"entry {Many - 1} ", StringComparison.Ordinal));
    window.Close();
  }

  /// <summary>A short transcript still renders completely: virtualization must not hide
  ///     entries that would have fitted anyway.</summary>
  [AvaloniaFact]
  public void A_Short_Transcript_Renders_All_Of_It()
  {
    (AgentTabViewModel _, Window window) = Show(3);

    IReadOnlyList<string> text = RealizedText(window);
    for (int i = 0; i < 3; i++)
    {
      Assert.Contains(text, t => t.Contains($"entry {i} ", StringComparison.Ordinal));
    }

    window.Close();
  }

  /// <summary>How many transcript entry views exist right now. Counted through the realized
  ///     containers rather than by type, because the templates differ per entry kind and the
  ///     question is how many were BUILT, not what they are.</summary>
  private static int RealizedEntryCount(Window window)
  {
    ItemsControl list = window.GetVisualDescendants().OfType<ItemsControl>()
        .First(c => c.Name == "TranscriptList");
    return list.GetRealizedContainers().Count();
  }

  private static IReadOnlyList<string> RealizedText(Window window) =>
      [.. window.GetVisualDescendants()
          .OfType<TextBlock>()
          .Select(t => t.Text ?? string.Empty)
          .Where(t => t.StartsWith("entry ", StringComparison.Ordinal))];
}
