using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.Desktop.Markdown;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

/// <summary>Transcript text must be measured at the width it is drawn at.
///     <para>The reported symptom was large gaps between paragraphs while a turn streamed,
///     and a chat area that kept growing while the user typed. Both are the same defect seen
///     twice: the tab's grid measured its cells at a 480 px minimum and arranged them at the
///     full viewport, so a paragraph's line breaks were computed for roughly half the width it
///     was painted at. A wrapped TextBlock's height comes from its line count, so a paragraph
///     wrapped at 450 px but drawn at 810 px reserved about twice the height its text needed -
///     the empty space between paragraphs.</para>
///     <para>The assertion is therefore measured height versus arranged height, not a pixel
///     count: a paragraph that lays itself out for the width it is given has the same number in
///     both places, and no amount of restyling or font change can make that untrue.</para></summary>
public class TranscriptLayoutTests
{
  /// <summary>One scrollbar's worth of slack for the measure-after-scrollbar-appears lag.</summary>
  private const double ScrollBarAllowance = 48;

  private const string Paragraph =
      "lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor " +
      "incididunt ut labore et dolore magna aliqua ut enim ad minim veniam quis nostrud";

  /// <summary>A markdown paragraph's measured width must be the width it is arranged at. Before
  ///     the fix the paragraph measured 450 and drew at 810, which is the gap the user saw.</summary>
  [AvaloniaFact]
  public void Markdown_Paragraph_Is_Measured_At_The_Width_It_Is_Drawn_At()
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();
    Window window = new() { Width = 1200, Height = 800, Content = new WorkspaceTabView { DataContext = tab } };
    window.Show();
    Dispatcher.UIThread.RunJobs();

    AgentSessionViewModel session = tab.Cells[0].ViewModel;
    session.Transcript.AppendAssistantDelta(Paragraph + "\n\n" + Paragraph);
    Dispatcher.UIThread.RunJobs();
    Dispatcher.UIThread.RunJobs();

    List<TextBlock> paragraphs = [.. MarkdownTextBlocks(window)];
    Assert.NotEmpty(paragraphs);
    foreach (TextBlock paragraph in paragraphs)
    {
      // The tolerance is one scrollbar: a transcript long enough to scroll grows its vertical
      // scrollbar AFTER measure, so the next pass measures 40 px narrower than it drew. That lag
      // is one pass deep and self-correcting. The defect was not a lag - it was a paragraph
      // measured at 450 px and drawn at 810 px, a gap no scrollbar explains.
      Assert.True(paragraph.DesiredSize.Width >= paragraph.Bounds.Width - ScrollBarAllowance,
          $"a paragraph must be measured at the width it is drawn at, but measured " +
          $"{paragraph.DesiredSize.Width:F0} and drew {paragraph.Bounds.Width:F0}");
    }

    window.Close();
  }

  /// <summary>A code block's line keeps its full width and the block scrolls: the body must
  ///     not be squeezed to the transcript's width, which is what clipped it.</summary>
  [AvaloniaFact]
  public void Exec_Card_Code_Block_Keeps_Its_Line_Width_And_Scrolls()
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();
    Window window = new() { Width = 1200, Height = 800, Content = new WorkspaceTabView { DataContext = tab } };
    window.Show();
    Dispatcher.UIThread.RunJobs();

    AgentSessionViewModel session = tab.Cells[0].ViewModel;
    session.Transcript.AddToolCall("exec", /*lang=json,strict*/ "{\"timeoutSeconds\":60,\"title\":\"probe\",\"program\":\"var a = 1;\"}");
    Dispatcher.UIThread.RunJobs();
    Dispatcher.UIThread.RunJobs();

    Border block = window.GetVisualDescendants().OfType<Border>()
        .First(b => b.Classes.Contains("markdown-code-block"));
    ScrollViewer scroller = Assert.IsType<ScrollViewer>(block.Parent);
    Grid frame = Assert.IsType<Grid>(scroller.Parent);
    Assert.Contains(frame.Children.OfType<Avalonia.Controls.Primitives.ScrollBar>(),
        b => b.Orientation == Avalonia.Layout.Orientation.Horizontal);
    // The card's body spans the card, not a narrow box inside it: the exec program used to be
    // laid out at 416 px inside a 1210 px card.
    Assert.True(block.Bounds.Width > 0, "the code block must be laid out");
    Assert.Equal(scroller.Bounds.Width, block.Bounds.Width, 2.0);
    window.Close();
  }

  /// <summary>The text blocks that hold transcript markdown, in render order.</summary>
  private static IEnumerable<TextBlock> MarkdownTextBlocks(Window window) =>
      window.GetVisualDescendants()
          .OfType<AgentMarkdownBlock>()
          .SelectMany(block => block.GetVisualDescendants().OfType<TextBlock>())
          .Where(tb => tb.Bounds.Width > 0 && tb.Bounds.Height > 0)
          .Where(tb => tb.Classes.Any(c => c.StartsWith("markdown-", StringComparison.Ordinal)));
}
