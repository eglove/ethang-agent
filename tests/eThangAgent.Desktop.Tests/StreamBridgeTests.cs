using eThangAgent.Desktop.Streaming;

namespace eThangAgent.Desktop.Tests;

public class StreamBridgeTests
{
  [Fact]
  public async Task Events_Are_Delivered_In_Publication_Order_Exactly_Once()
  {
    List<UiStreamEvent> received = [];
    StreamBridge bridge = new(e =>
    {
      received.Add(e);
      return Task.CompletedTask;
    });
    bridge.Start();
    bridge.OnContentDelta("a");
    bridge.OnReasoningDelta("r");
    bridge.OnIterationEnd();
    bridge.OnContentDelta("b");
    bridge.OnToolCall("read", "{}");
    bridge.OnToolResult("read", "ok", "full result", false);
    bridge.MarkTurnComplete();
    await bridge.DrainUntilIdleAsync();

    Assert.Equal(6, received.Count);
    _ = Assert.IsType<UiStreamEvent.Delta>(received[0]);
    _ = Assert.IsType<UiStreamEvent.Reasoning>(received[1]);
    _ = Assert.IsType<UiStreamEvent.IterationEnd>(received[2]);
    _ = Assert.IsType<UiStreamEvent.Delta>(received[3]);
    _ = Assert.IsType<UiStreamEvent.ToolCallEvent>(received[4]);
    _ = Assert.IsType<UiStreamEvent.ToolResultEvent>(received[5]);
  }

  /// <summary>
  /// If the sink throws, DrainUntilIdleAsync must surface the exception rather than
  /// hanging forever. A timeout-guarded CancellationToken ensures CI cannot deadlock
  /// on a regression: the test fails fast (&lt;5 s) with OperationCanceledException
  /// when the bug is present instead of blocking the run.
  /// </summary>
  [Fact]
  public async Task Throwing_Sink_Faults_DrainUntilIdleAsync_Instead_Of_Hanging()
  {
    using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
    static Task throwingSink(UiStreamEvent _) => throw new InvalidOperationException("boom");
    StreamBridge bridge = new(throwingSink);
    bridge.Start();
    bridge.OnContentDelta("x");
    bridge.MarkTurnComplete();

    // DrainUntilIdleAsync must complete (with an exception) — not hang.
    // We race it against the timeout so a regression causes a fast, clear failure.
    Task drainTask = bridge.DrainUntilIdleAsync();
    Task completed = await Task.WhenAny(drainTask, Task.Delay(Timeout.Infinite, cts.Token)
        .ContinueWith(_ => Task.CompletedTask, CancellationToken.None,
            TaskContinuationOptions.OnlyOnCanceled, TaskScheduler.Default)
        .Unwrap());
    Assert.Same(drainTask, completed); // timed out → regression
    _ = await Assert.ThrowsAsync<InvalidOperationException>(() => drainTask);
  }

  [Fact]
  public async Task Events_Published_From_Many_Threads_All_Arrive()
  {
    List<UiStreamEvent> received = [];
    StreamBridge bridge = new(e =>
    {
      received.Add(e);
      return Task.CompletedTask;
    });
    bridge.Start();
    Task[] tasks = [.. Enumerable.Range(0, 8).Select(i => Task.Run(() =>
    {
      for (int j = 0; j < 50; j++)
      {
        bridge.OnContentDelta(i + ":" + j);
      }
    }))];
    await Task.WhenAll(tasks);
    bridge.MarkTurnComplete();
    await bridge.DrainUntilIdleAsync();
    Assert.Equal(400, received.Count);
    Assert.Equal(400, received.Select(e => ((UiStreamEvent.Delta)e).Text).Distinct().Count());
  }
}
