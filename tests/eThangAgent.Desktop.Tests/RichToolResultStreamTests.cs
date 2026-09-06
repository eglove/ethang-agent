using System.Threading.Channels;
using eThangAgent.Desktop.Streaming;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

/// <summary>The rich tool-result metadata (title, display body) rides the bridge and
///     the coalescer verbatim and lands on the transcript as card content: the header
///     shows the title, the body shows the program fenced as C#.</summary>
public class RichToolResultStreamTests
{
  [Fact]
  public async Task Bridge_ToolResult_CarriesTitleAndDisplayBody()
  {
    Channel<UiStreamEvent> channel = Channel.CreateUnbounded<UiStreamEvent>();
    StreamBridge bridge = new(evt => channel.Writer.WriteAsync(evt).AsTask());
    bridge.Start();

    bridge.OnToolResult("exec", "ok", "42", false, "parse names");
    bridge.MarkTurnComplete();
    await bridge.DrainUntilIdleAsync();

    UiStreamEvent.ToolResultEvent evt = Assert.IsType<UiStreamEvent.ToolResultEvent>(await channel.Reader.ReadAsync(TestContext.Current.CancellationToken));
    Assert.Equal("parse names", evt.Title);
  }

  [Fact]
  public void Transcript_ToolResult_RendersTitleInHeader_AndProgramInBody()
  {
    TranscriptViewModel vm = new();

    vm.AddToolResult("exec", "ok", "42", false, "parse names");

    ToolResultEntry entry = Assert.IsType<ToolResultEntry>(Assert.Single(vm.Entries));
    Assert.Equal("parse names", entry.HeaderTitle);
    Assert.Equal("42", entry.FullContent);
  }

  [Fact]
  public void Transcript_PlainToolResult_KeepsLegacyHeader_AndNoProgramBody()
  {
    TranscriptViewModel vm = new();

    vm.AddToolResult("read", "12 lines", "1: first", false);

    ToolResultEntry entry = Assert.IsType<ToolResultEntry>(Assert.Single(vm.Entries));
    Assert.Equal("", entry.HeaderTitle);

  }

  [Fact]
  public async Task SessionVm_Event_RendersRichToolCard()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel();

    await vm.ApplyUiStreamEventAsync(new UiStreamEvent.ToolResultEvent("exec", "ok", "42", false, "parse names"));

    ToolResultEntry entry = Assert.IsType<ToolResultEntry>(Assert.Single(vm.Transcript.Entries));
    Assert.Equal("parse names", entry.HeaderTitle);
  }
}
