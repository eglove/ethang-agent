using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.Desktop.Markdown;
using MarkView.Avalonia;
using MarkView.Avalonia.Rendering;
using MarkView.Avalonia.Rendering.Inlines;

namespace eThangAgent.Desktop.Tests;

/// <summary>Contract for the MarkView-backed transcript block: self-rendering on
///     property change (the UI only binds), live markdown while open with the
///     re-render coalesced by ThrottleInterval (deltas are cheap, visual rebuilds
///     are not), an immediate render on close, and link routing through the
///     http(s)-only launcher seam.</summary>
public class AgentMarkdownBlockTests
{
  [AvaloniaFact]
  public void Open_Block_Renders_Live_Markdown_Immediately_On_First_Content()
  {
    Window window = new() { Width = 400, Height = 300 };
    AgentMarkdownBlock block = new() { MarkdownText = "# Hi\n\n**bold**", IsOpen = true };
    window.Content = block;
    window.Show();
    Dispatcher.UIThread.RunJobs();

    MarkdownViewer viewer = Assert.IsType<MarkdownViewer>(block.Content);
    Assert.Equal("# Hi\n\n**bold**", viewer.Markdown);
  }

  [AvaloniaFact]
  public void Rapid_Delta_Within_Throttle_Defers_Render_Until_Flush()
  {
    Window window = new() { Width = 400, Height = 300 };
    AgentMarkdownBlock block = new()
    {
      MarkdownText = "one",
      IsOpen = true,
      ThrottleInterval = TimeSpan.FromMilliseconds(10_000),
    };
    window.Content = block;
    window.Show();
    Dispatcher.UIThread.RunJobs();
    Assert.Equal("one", Assert.IsType<MarkdownViewer>(block.Content).Markdown);

    block.MarkdownText = "two";
    Dispatcher.UIThread.RunJobs();
    Assert.Equal("one", Assert.IsType<MarkdownViewer>(block.Content).Markdown); // deferred

    block.FlushPendingRender();
    Assert.Equal("two", Assert.IsType<MarkdownViewer>(block.Content).Markdown); // timer tick
  }

  [AvaloniaFact]
  public void Close_Renders_Final_Markdown_Immediately_Even_When_Throttled()
  {
    Window window = new() { Width = 400, Height = 300 };
    AgentMarkdownBlock block = new()
    {
      MarkdownText = "partial",
      IsOpen = true,
      ThrottleInterval = TimeSpan.FromMilliseconds(10_000),
    };
    window.Content = block;
    window.Show();
    Dispatcher.UIThread.RunJobs();

    block.MarkdownText = "final **x**"; // within throttle: deferred
    Dispatcher.UIThread.RunJobs();
    block.IsOpen = false; // close is terminal: renders now

    Assert.Equal("final **x**", Assert.IsType<MarkdownViewer>(block.Content).Markdown);
    block.FlushPendingRender(); // pending must have been cleared
    Assert.Equal("final **x**", Assert.IsType<MarkdownViewer>(block.Content).Markdown);
  }

  [AvaloniaFact]
  public void Open_Reasoning_Renders_Plain_Italic_Text()
  {
    Window window = new() { Width = 400, Height = 300 };
    AgentMarkdownBlock block = new()
    {
      MarkdownText = "thinking partial **text",
      IsOpen = true,
      Variant = AgentMarkdownVariant.Reasoning,
    };
    window.Content = block;
    window.Show();
    Dispatcher.UIThread.RunJobs();

    TextBlock plain = Assert.IsType<TextBlock>(block.Content);
    Assert.Equal(FontStyle.Italic, plain.FontStyle);
    Assert.Equal("thinking partial **text", plain.Text);
  }

  [AvaloniaFact]
  public void Closed_Reasoning_Renders_Markdown_Dimmed()
  {
    Window window = new() { Width = 400, Height = 300 };
    AgentMarkdownBlock block = new()
    {
      MarkdownText = "done **thought**",
      IsOpen = false,
      Variant = AgentMarkdownVariant.Reasoning,
    };
    window.Content = block;
    window.Show();
    Dispatcher.UIThread.RunJobs();

    MarkdownViewer viewer = Assert.IsType<MarkdownViewer>(block.Content);
    Assert.Equal(0.7, viewer.Opacity);
  }

  [AvaloniaFact]
  public void Rendered_Link_Lands_In_A_TextBlock_As_A_Hyperlink_Span()
  {
    Window window = new() { Width = 400, Height = 300 };
    AgentMarkdownBlock block = new() { MarkdownText = "see [docs](https://example.com/a) now", IsOpen = false };
    window.Content = block;
    window.Show();
    Dispatcher.UIThread.RunJobs();

    MarkdownHyperlink link = Descendants<TextBlock>(window)
        .SelectMany(tb => tb.Inlines ?? [])
        .OfType<MarkdownHyperlink>()
        .Single();
    Assert.Equal(new Uri("https://example.com/a"), link.NavigateUri);
  }

  [AvaloniaFact]
  public void Viewer_Link_Event_Routes_Through_Launcher_Seam()
  {
    string? launched = null;
    MarkdownLinkLauncher.Override = url =>
    {
      launched = url;
      return true;
    };
    try
    {
      Window window = new() { Width = 400, Height = 300 };
      AgentMarkdownBlock block = new() { MarkdownText = "see [docs](https://example.com/a) now", IsOpen = false };
      window.Content = block;
      window.Show();
      Dispatcher.UIThread.RunJobs();

      MarkdownViewer viewer = Assert.IsType<MarkdownViewer>(block.Content);
      viewer.RaiseEvent(new LinkClickedEventArgs("https://example.com/a") { RoutedEvent = MarkdownViewer.LinkClickedEvent });
      Dispatcher.UIThread.RunJobs();

      Assert.Equal("https://example.com/a", launched);
    }
    finally
    {
      MarkdownLinkLauncher.Override = null;
    }
  }

  [AvaloniaFact]
  public void Viewer_Non_Http_Link_Never_Launches()
  {
    string? launched = null;
    MarkdownLinkLauncher.Override = url =>
    {
      launched = url;
      return true;
    };
    try
    {
      Window window = new() { Width = 400, Height = 300 };
      AgentMarkdownBlock block = new() { MarkdownText = "see [x](javascript:alert(1))", IsOpen = false };
      window.Content = block;
      window.Show();
      Dispatcher.UIThread.RunJobs();

      MarkdownViewer viewer = Assert.IsType<MarkdownViewer>(block.Content);
      viewer.RaiseEvent(new LinkClickedEventArgs("javascript:alert(1)") { RoutedEvent = MarkdownViewer.LinkClickedEvent });
      Dispatcher.UIThread.RunJobs();

      // The launcher gate rejects the scheme before the override is consulted.
      Assert.Null(launched);
    }
    finally
    {
      MarkdownLinkLauncher.Override = null;
    }
  }

  [Fact]
  public void Https_Is_The_Gate_Scheme()
  {
    MarkdownLinkLauncher.Override = _ => true;
    try
    {
      Assert.True(MarkdownLinkLauncher.TryOpen("https://example.com"));
      Assert.False(MarkdownLinkLauncher.TryOpen("file:///C:/Windows"));
      Assert.False(MarkdownLinkLauncher.TryOpen("not a url"));
    }
    finally
    {
      MarkdownLinkLauncher.Override = null;
    }
  }

  [AvaloniaFact]
  public void Delta_Burst_Renders_Once_While_Open_And_Final_On_Close()
  {
    Window window = new() { Width = 400, Height = 300 };
    AgentMarkdownBlock block = new() { MarkdownText = "start", IsOpen = true };
    window.Content = block;
    window.Show();
    Dispatcher.UIThread.RunJobs();

    // A live stream: 200 Replace deltas against the open block. The coalescing
    // throttle must keep the visual rebuilds bounded - only the initial render
    // lands while streaming; nothing else until the clock is collapsed.
    for (int i = 0; i < 200; i++)
    {
      block.MarkdownText = "delta " + i + " **x**";
      Dispatcher.UIThread.RunJobs();
    }

    Assert.Equal("start", Assert.IsType<MarkdownViewer>(block.Content).Markdown);

    block.IsOpen = false;
    Dispatcher.UIThread.RunJobs();
    Assert.Equal("delta 199 **x**", Assert.IsType<MarkdownViewer>(block.Content).Markdown);
  }
  private static IEnumerable<T> Descendants<T>(Control root) where T : Control
  {
    foreach (object child in root.GetVisualChildren())
    {
      if (child is Control c)
      {
        if (c is T match)
        {
          yield return match;
        }

        foreach (T nested in Descendants<T>(c))
        {
          yield return nested;
        }
      }
    }
  }
}
