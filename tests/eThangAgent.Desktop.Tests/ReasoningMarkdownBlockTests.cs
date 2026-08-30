using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using eThangAgent.Desktop.Markdown;

namespace eThangAgent.Desktop.Tests;

/// <summary>Reasoning block rendering contract: plain single Run while streaming,
///     full markdown parse on close - identical lifecycle to the assistant block.
///     The streamed open path must never re-parse markdown (a wedging re-layout of
///     the whole growing block); styling stays italic/dim via control defaults.</summary>
public class ReasoningMarkdownBlockTests
{
  [AvaloniaFact]
  public void Open_Block_Renders_A_Single_Plain_Run_While_Streaming()
  {
    ReasoningMarkdownBlock block = new() { MarkdownText = "thinking **hard** now", IsOpen = true };
    InlineCollection rendered = block.Inlines!;
    Run plain = Assert.IsType<Run>(Assert.Single(rendered));
    Assert.Equal("thinking **hard** now", plain.Text);
  }

  [AvaloniaFact]
  public void Open_Block_Stays_Italic_And_Dim()
  {
    ReasoningMarkdownBlock block = new() { MarkdownText = "hmm", IsOpen = true };
    Assert.Equal(FontStyle.Italic, block.FontStyle);
    Assert.True(block.Opacity < 1.0, $"reasoning stays dim, Opacity={block.Opacity}");
  }

  [AvaloniaFact]
  public void MarkdownText_Change_Rerenders_Plain_Without_Explicit_Call()
  {
    ReasoningMarkdownBlock block = new() { MarkdownText = "first", IsOpen = true };
    block.MarkdownText = "second plain";
    InlineCollection rendered = block.Inlines!;
    Run plain = Assert.IsType<Run>(Assert.Single(rendered));
    Assert.Equal("second plain", plain.Text);
  }

  [AvaloniaFact]
  public void IsOpen_False_Change_Renders_Final_Markdown()
  {
    ReasoningMarkdownBlock block = new() { MarkdownText = "done `code`", IsOpen = true };
    _ = Assert.IsType<Run>(Assert.Single(block.Inlines!));
    block.IsOpen = false;
    InlineCollection rendered = block.Inlines!;
    Run mono = Assert.IsType<Run>(Assert.Single(rendered.OfType<Run>(), r => r.FontFamily == TestFonts.Mono));
    Assert.Equal("code", mono.Text);
  }

  [AvaloniaFact]
  public void Closed_Block_Renders_Markdown_Inlines()
  {
    ReasoningMarkdownBlock block = new() { MarkdownText = "done **bold**", IsOpen = false };
    InlineCollection rendered = block.Inlines!;
    Run bold = Assert.IsType<Run>(Assert.Single(rendered.OfType<Run>(), r => r.FontWeight == FontWeight.Bold));
    Assert.Equal("bold", bold.Text);
    Assert.DoesNotContain(rendered, i => i is Run { Text: "done **bold**" });
  }
}
