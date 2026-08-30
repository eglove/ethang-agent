using eThangAgent.Desktop.Streaming;

namespace eThangAgent.Desktop.Tests;

public class ToolResultEventTests
{
  [Fact]
  public async Task OnToolResult_Carries_FullContent_And_ErrorFlag_Through_The_Bridge()
  {
    List<UiStreamEvent> received = [];
    StreamBridge bridge = new(e =>
    {
      received.Add(e);
      return Task.CompletedTask;
    });
    bridge.Start();
    bridge.OnToolResult("read", "ok", "line one\nline two", false);
    bridge.MarkTurnComplete();
    await bridge.DrainUntilIdleAsync();

    UiStreamEvent.ToolResultEvent evt = Assert.IsType<UiStreamEvent.ToolResultEvent>(Assert.Single(received));
    Assert.Equal("read", evt.Name);
    Assert.Equal("ok", evt.Summary);
    Assert.Equal("line one\nline two", evt.FullContent);
    Assert.False(evt.IsError);
  }
}
