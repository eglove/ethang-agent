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
    // The throttle window is set WIDE so no timer tick can fire while the burst runs:
    // the contract under test is the coalescing SHAPE (many deltas, few rebuilds). A
    // 150 ms default made the assertion depend on how fast this machine drained 200
    // dispatcher jobs - a wall-clock assumption the control never made.
    AgentMarkdownBlock block = new()
    {
      MarkdownText = "start",
      IsOpen = true,
      ThrottleInterval = TimeSpan.FromSeconds(60),
    };
    window.Content = block;
    window.Show();
    Dispatcher.UIThread.RunJobs();
    Assert.Equal(1, block.RenderCount);

    // A live stream: 200 Replace deltas against the open block. The coalescing throttle
    // must keep the visual rebuilds BOUNDED - the initial render lands, every later
    // delta only marks the block dirty, and nothing else rebuilds until the window
    // closes. Asserting a bounded COUNT (not 'the text never changed') is the real
    // contract: a value read back from the viewer only says which delta happened to be
    // pending the last time the clock fired.
    for (int i = 0; i < 200; i++)
    {
      block.MarkdownText = "delta " + i + " **x**";
      Dispatcher.UIThread.RunJobs();
    }

    Assert.Equal(1, block.RenderCount);
    Assert.Equal("start", Assert.IsType<MarkdownViewer>(block.Content).Markdown);

    block.IsOpen = false;
    Dispatcher.UIThread.RunJobs();
    Assert.Equal("delta 199 **x**", Assert.IsType<MarkdownViewer>(block.Content).Markdown);
    Assert.Equal(2, block.RenderCount); // the close render is the only one the burst bought
  }

  /// <summary>The coalescing bound as a CONTRACT, at the control's REAL default interval:
  ///     200 deltas must cost a small constant number of rebuilds, never one per delta.
  ///     This is the assertion the throttle exists to satisfy, and unlike a fixed expected
  ///     count it holds however long the burst takes on a given machine - the allowance
  ///     grows with the elapsed clock, so what it catches is a coalescer that stopped
  ///     coalescing rather than a machine that ran slowly.
  ///     <para>It is also the RED half of the regression pair: against a control that
  ///     rebuilt per delta (<c>RenderCount</c> reaching 200) the bound fails loudly.</para></summary>
  [AvaloniaFact]
  public void Delta_Burst_AtTheDefaultInterval_RebuildsFarFewerTimesThanThereAreDeltas()
  {
    Window window = new() { Width = 400, Height = 300 };
    AgentMarkdownBlock block = new() { MarkdownText = "start", IsOpen = true };
    Assert.Equal(TimeSpan.FromMilliseconds(150), block.ThrottleInterval); // the production default
    window.Content = block;
    window.Show();
    Dispatcher.UIThread.RunJobs();

    const int deltas = 200;
    System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    for (int i = 0; i < deltas; i++)
    {
      block.MarkdownText = "delta " + i + " **x**";
      Dispatcher.UIThread.RunJobs();
    }

    clock.Stop();
    int renders = block.RenderCount;

    // One rebuild per closed throttle window, plus the initial one and one in flight.
    // The point is that the count tracks TIME while the delta count is 200: the two
    // cannot be confused, which is exactly what the old 'text still says start'
    // assertion did.
    double windows = Math.Ceiling(clock.Elapsed.TotalMilliseconds / block.ThrottleInterval.TotalMilliseconds);
    int bound = (int)windows + 2;
    Assert.True(renders <= bound,
        $"a {deltas}-delta burst taking {clock.Elapsed.TotalMilliseconds:F0} ms must rebuild at most "
        + $"{bound} times ({block.ThrottleInterval.TotalMilliseconds:F0} ms windows), but rebuilt {renders} times");
    Assert.True(renders < deltas, $"the throttle must coalesce: {renders} rebuilds for {deltas} deltas");

    block.IsOpen = false;
    Dispatcher.UIThread.RunJobs();
    Assert.Equal("delta 199 **x**", Assert.IsType<MarkdownViewer>(block.Content).Markdown);
  }

  /// <summary>The other side of the counter: a CLOSED block rebuilds on every change
  ///     (the throttle is open-block only), so the counter must actually move - otherwise
  ///     the bounded-count assertions above could pass against a dead counter that never
  ///     increments, which is the failure mode a 'renders stayed at 1' test cannot see.</summary>
  [AvaloniaFact]
  public void RenderCount_MovesForEveryChange_WhenTheBlockIsClosed()
  {
    Window window = new() { Width = 400, Height = 300 };
    AgentMarkdownBlock block = new() { MarkdownText = "one", IsOpen = false };
    window.Content = block;
    window.Show();
    Dispatcher.UIThread.RunJobs();
    int afterFirst = block.RenderCount;

    block.MarkdownText = "two";
    Dispatcher.UIThread.RunJobs();
    block.MarkdownText = "three";
    Dispatcher.UIThread.RunJobs();

    Assert.Equal(afterFirst + 2, block.RenderCount);
    Assert.Equal("three", Assert.IsType<MarkdownViewer>(block.Content).Markdown);
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
