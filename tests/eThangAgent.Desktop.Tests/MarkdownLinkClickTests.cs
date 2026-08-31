using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using eThangAgent.Desktop.Markdown;

namespace eThangAgent.Desktop.Tests;

/// <summary>Click-to-launch contract for rendered markdown links: a click on the
///     link block hands the URL to the launcher seam; non-http(s) schemes never
///     launch. The launcher override is swapped per test and cleared in finally.</summary>
public class MarkdownLinkClickTests
{
  [AvaloniaFact]
  public void Clicking_Link_Hands_Url_To_Launcher()
  {
    string? launched = null;
    MarkdownLinkLauncher.Override = url =>
    {
      launched = url;
      return true;
    };
    try
    {
      Window window = new() { Width = 400, Height = 200, Content = ClosedBlock("see [docs](https://example.com/a) now") };
      window.Show();
      Dispatcher.UIThread.RunJobs();
      MarkdownLinkBlock link = FindLink(window);
      window.MouseDown(CenterInWindow(link, window), MouseButton.Left, RawInputModifiers.None);
      window.MouseUp(CenterInWindow(link, window), MouseButton.Left, RawInputModifiers.None);
      Dispatcher.UIThread.RunJobs();
      Assert.Equal("https://example.com/a", launched);
    }
    finally
    {
      MarkdownLinkLauncher.Override = null;
    }
  }

  [AvaloniaFact]
  public void Clicking_Non_Http_Link_Never_Launches()
  {
    string? launched = null;
    MarkdownLinkLauncher.Override = url =>
    {
      launched = url;
      return true;
    };
    try
    {
      Window window = new() { Width = 400, Height = 200, Content = ClosedBlock("see [x](javascript:alert(1)) now") };
      window.Show();
      Dispatcher.UIThread.RunJobs();
      MarkdownLinkBlock link = FindLink(window);
      window.MouseDown(CenterInWindow(link, window), MouseButton.Left, RawInputModifiers.None);
      window.MouseUp(CenterInWindow(link, window), MouseButton.Left, RawInputModifiers.None);
      Dispatcher.UIThread.RunJobs();
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

  private static AssistantMarkdownBlock ClosedBlock(string markdown) =>
      new() { MarkdownText = markdown, IsOpen = false };

  private static MarkdownLinkBlock FindLink(Window window)
  {
    AssistantMarkdownBlock block = Assert.IsType<AssistantMarkdownBlock>(window.Content);
    InlineUIContainer container = Assert.Single(block.Inlines!.OfType<InlineUIContainer>());
    return Assert.IsType<MarkdownLinkBlock>(container.Child);
  }

  private static Point CenterInWindow(Control control, Window window)
      => control.TranslatePoint(
          new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
          ?? throw new InvalidOperationException("control not laid out inside the window");
}
