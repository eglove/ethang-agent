using Avalonia.Controls.Documents;
using Avalonia.Media;
using eThangAgent.Desktop.Markdown;

namespace eThangAgent.Desktop.Tests;

public class AssistantMarkdownBlockTests
{
  // Regression: the block must render from property changes alone - the UI binds
  // MarkdownText/IsOpen and never calls Render. (Tests below that call Render()
  // directly verify content, not the production trigger.)

  [Fact]
  public void MarkdownText_Change_Renders_Plain_Text_Without_Explicit_Render()
  {
    AssistantMarkdownBlock block = new() { MarkdownText = "streaming plain" };
    Run run = Assert.IsType<Run>(Assert.Single(block.Inlines!));
    Assert.Equal("streaming plain", run.Text);
  }

  [Fact]
  public void IsOpen_False_Change_Replaces_Plain_Run_With_Markdown()
  {
    AssistantMarkdownBlock block = new() { MarkdownText = "done **bold**" };
    _ = Assert.IsType<Run>(Assert.Single(block.Inlines!)); // plain Run while open - no parse
    block.IsOpen = false;
    Run bold = Assert.IsType<Run>(Assert.Single(block.Inlines!.OfType<Run>(), r => r.FontWeight == FontWeight.Bold));
    Assert.Equal("bold", bold.Text);
    Assert.DoesNotContain(block.Inlines!, i => i is Run { Text: "done **bold**" });
  }

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
