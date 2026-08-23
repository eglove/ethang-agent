using System.Threading.Channels;
using eThangAgent.Desktop.Streaming;

namespace eThangAgent.Desktop.Tests;

public class StreamBridgeTests
{
    [Fact]
    public async Task Events_Are_Delivered_In_Publication_Order_Exactly_Once()
    {
        var received = new List<UiStreamEvent>();
        var bridge = new StreamBridge(e => received.Add(e));
        bridge.Start();
        bridge.OnContentDelta("a");
        bridge.OnReasoningDelta("r");
        bridge.OnIterationEnd();
        bridge.OnContentDelta("b");
        bridge.OnToolCall("read", "{}");
        bridge.OnToolResult("read", "ok");
        bridge.MarkTurnComplete();
        await bridge.DrainUntilIdleAsync();

        Assert.Equal(6, received.Count);
        Assert.IsType<UiStreamEvent.Delta>(received[0]);
        Assert.IsType<UiStreamEvent.Reasoning>(received[1]);
        Assert.IsType<UiStreamEvent.IterationEnd>(received[2]);
        Assert.IsType<UiStreamEvent.Delta>(received[3]);
        Assert.IsType<UiStreamEvent.ToolCallEvent>(received[4]);
        Assert.IsType<UiStreamEvent.ToolResultEvent>(received[5]);
    }

    [Fact]
    public async Task Events_Published_From_Many_Threads_All_Arrive()
    {
        var received = new List<UiStreamEvent>();
        var bridge = new StreamBridge(e => received.Add(e));
        bridge.Start();
        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            for (var j = 0; j < 50; j++) bridge.OnContentDelta(i + ":" + j);
        })).ToArray();
        await Task.WhenAll(tasks);
        bridge.MarkTurnComplete();
        await bridge.DrainUntilIdleAsync();
        Assert.Equal(400, received.Count);
        Assert.Equal(400, received.Select(e => ((UiStreamEvent.Delta)e).Text).Distinct().Count());
    }
}
