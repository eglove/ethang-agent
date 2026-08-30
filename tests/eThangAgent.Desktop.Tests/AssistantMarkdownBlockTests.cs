using Avalonia.Controls.Documents;
using Avalonia.Media;
using eThangAgent.Desktop.Markdown;

namespace eThangAgent.Desktop.Tests;

public class AssistantMarkdownBlockTests
{
  [Fact]
  public void Open_Block_Renders_Plain_Text_While_Streaming()
  {
    AssistantMarkdownBlock block = new() { MarkdownText = "partial **text", IsOpen = true };
    block.Render();
    InlineCollection rendered = block.Inlines!;
    Run run = Assert.IsType<Run>(Assert.Single(rendered));
    Assert.Equal("partial **text", run.Text);
    Assert.NotEqual(FontWeight.Bold, run.FontWeight);
  }

  [Fact]
  public void Closed_Block_Renders_Markdown_Inlines()
  {
    AssistantMarkdownBlock block = new() { MarkdownText = "done **bold**", IsOpen = false };
    block.Render();
    InlineCollection rendered = block.Inlines!;
    Run bold = Assert.IsType<Run>(Assert.Single(rendered.OfType<Run>(), r => r.FontWeight == FontWeight.Bold));
    Assert.Equal("bold", bold.Text);
  }

  [Fact]
  public void ReRendering_With_Different_State_Replaces_The_Inlines()
  {
    AssistantMarkdownBlock block = new() { MarkdownText = "streaming plain", IsOpen = true };
    block.Render();
    _ = Assert.Single(block.Inlines!);
    block.MarkdownText = "final `code`";
    block.IsOpen = false;
    block.Render();
    InlineCollection rendered = block.Inlines!;
    Run mono = Assert.IsType<Run>(Assert.Single(rendered.OfType<Run>(), x => x.FontFamily == TestFonts.Mono));
    Assert.Equal("final ", Assert.IsType<Run>(rendered[0]).Text);
    Assert.Equal(TestFonts.Mono, mono.FontFamily);
  }
}
