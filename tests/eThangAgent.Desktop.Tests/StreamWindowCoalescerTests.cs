using eThangAgent.Desktop.Streaming;

namespace eThangAgent.Desktop.Tests;

/// <summary>
/// Time-slice contract for stream coalescing: text deltas and reasoning deltas
/// arriving inside one window deliver to the sink as a single merged event;
/// structural events (tool call, tool result, iteration end, notice) force an
/// immediate flush so ordering and block-close semantics never change. The clock
/// is injected, so no test waits on real time.
/// </summary>
public class StreamWindowCoalescerTests
{
  private sealed class RecordingSink(List<UiStreamEvent> received) : IStreamSink
  {
    public Task DeliverAsync(UiStreamEvent evt)
    {
      received.Add(evt);
      return Task.CompletedTask;
    }
  }

  private sealed class FakeClock
  {
    public double Now { get; set; }

    public double ClockRead() => Now;
  }

  private static (StreamWindowCoalescer Coalescer, List<UiStreamEvent> Received, FakeClock Clock) Create()
  {
    FakeClock clock = new();
    List<UiStreamEvent> received = [];
    StreamWindowCoalescer coalescer = new(
        new RecordingSink(received),
        windowSeconds: 0.08,
        clock.ClockRead);
    return (coalescer, received, clock);
  }

  /// <summary>Names the production change that would fail this test: removing the
  ///     window merge (delivering each delta directly) delivers 3 events instead of 1.</summary>
  [Fact]
  public async Task Deltas_Within_One_Window_Merge_Into_A_Single_Delivery()
  {
    (StreamWindowCoalescer coalescer, List<UiStreamEvent> received, _) = Create();

    await coalescer.ContentDeltaAsync("a");
    await coalescer.ContentDeltaAsync("b");
    await coalescer.ContentDeltaAsync("c");
    await coalescer.FlushAsync();

    UiStreamEvent.Delta delta = Assert.IsType<UiStreamEvent.Delta>(Assert.Single(received));
    Assert.Equal("abc", delta.Text);
  }

  /// <summary>Advancing the clock past the window closes the slice: the buffered
  ///     text delivers as one event and the next delta starts a fresh buffer.</summary>
  [Fact]
  public async Task Window_Boundary_Starts_A_New_Slice()
  {
    (StreamWindowCoalescer coalescer, List<UiStreamEvent> received, FakeClock clock) = Create();

    await coalescer.ContentDeltaAsync("a");
    clock.Now = 0.09; // past the 80 ms window
    await coalescer.ContentDeltaAsync("b");
    await coalescer.FlushAsync();

    Assert.Equal(2, received.Count);
    Assert.Equal("a", Assert.IsType<UiStreamEvent.Delta>(received[0]).Text);
    Assert.Equal("b", Assert.IsType<UiStreamEvent.Delta>(received[1]).Text);
  }

  [Fact]
  public async Task Reasoning_Deltas_Merge_The_Same_Way()
  {
    (StreamWindowCoalescer coalescer, List<UiStreamEvent> received, _) = Create();

    await coalescer.ReasoningDeltaAsync("x");
    await coalescer.ReasoningDeltaAsync("y");
    await coalescer.FlushAsync();

    Assert.Equal("xy", Assert.IsType<UiStreamEvent.Reasoning>(Assert.Single(received)).Text);
  }

  /// <summary>Reasoning and content must never merge into one event: a reasoning
  ///     delta followed by a content delta flushes the reasoning slice first.
  ///     Regression guard for a content run rendered inside an open reasoning block.</summary>
  [Fact]
  public async Task Content_After_Reasoning_Flushes_The_Reasoning_Slice_First()
  {
    (StreamWindowCoalescer coalescer, List<UiStreamEvent> received, _) = Create();

    await coalescer.ReasoningDeltaAsync("think");
    await coalescer.ContentDeltaAsync("answer");
    await coalescer.FlushAsync();

    Assert.Equal(2, received.Count);
    Assert.Equal("think", Assert.IsType<UiStreamEvent.Reasoning>(received[0]).Text);
    Assert.Equal("answer", Assert.IsType<UiStreamEvent.Delta>(received[1]).Text);
  }

  /// <summary>Structural events never buffer: they flush any pending slice first,
  ///     then deliver verbatim, in order. This is what keeps iteration end closing
  ///     the open block before the next content slice opens a new one.</summary>
  [Fact]
  public async Task Structural_Event_Flushes_Pending_Text_Then_Delivers_Verbatim()
  {
    (StreamWindowCoalescer coalescer, List<UiStreamEvent> received, _) = Create();

    await coalescer.ContentDeltaAsync("pre");
    await coalescer.IterationEndAsync();
    await coalescer.ContentDeltaAsync("post");
    await coalescer.FlushAsync();

    Assert.Equal(3, received.Count);
    Assert.Equal("pre", Assert.IsType<UiStreamEvent.Delta>(received[0]).Text);
    _ = Assert.IsType<UiStreamEvent.IterationEnd>(received[1]);
    Assert.Equal("post", Assert.IsType<UiStreamEvent.Delta>(received[2]).Text);
  }

  [Fact]
  public async Task Tool_Call_And_Result_And_Notice_Deliver_Verbatim_After_A_Flush()
  {
    (StreamWindowCoalescer coalescer, List<UiStreamEvent> received, _) = Create();

    await coalescer.ContentDeltaAsync("a");
    await coalescer.ToolCallAsync("read", /*lang=json,strict*/ "{\"path\":\"x\"}");
    await coalescer.ToolResultAsync("read", "ok", "full", false);
    await coalescer.NoticeAsync("note");

    Assert.Equal(4, received.Count);
    Assert.Equal("a", Assert.IsType<UiStreamEvent.Delta>(received[0]).Text);
    UiStreamEvent.ToolCallEvent call = Assert.IsType<UiStreamEvent.ToolCallEvent>(received[1]);
    Assert.Equal("read", call.Name);
    Assert.Equal(/*lang=json,strict*/ "{\"path\":\"x\"}", call.Arguments);
    UiStreamEvent.ToolResultEvent result = Assert.IsType<UiStreamEvent.ToolResultEvent>(received[2]);
    Assert.Equal("full", result.FullContent);
    Assert.Equal("note", Assert.IsType<UiStreamEvent.Notice>(received[3]).Text);
  }

  /// <summary>An empty buffer flushes nothing: a structural event with no pending
  ///     text must not inject a zero-length delta.</summary>
  [Fact]
  public async Task Flush_With_Empty_Buffer_Delivers_Nothing()
  {
    (StreamWindowCoalescer coalescer, List<UiStreamEvent> received, _) = Create();

    await coalescer.IterationEndAsync();
    await coalescer.FlushAsync();

    _ = Assert.IsType<UiStreamEvent.IterationEnd>(Assert.Single(received));
  }

  /// <summary>The bounded-buffer guard: fragments are checked against the ceiling
  ///     BEFORE enqueue, so accumulation - the O(n^2) render driver - stays bounded
  ///     no matter the arrival pattern; a burst of large fragments flushes at the
  ///     ceiling instead of merging past it.</summary>
  [Fact]
  public async Task Buffer_Exceeding_MaxChars_Flushes_At_The_Ceiling()
  {
    (StreamWindowCoalescer coalescer, List<UiStreamEvent> received, _) = Create();
    string big = new('w', 20_000);

    await coalescer.ContentDeltaAsync(big);
    await coalescer.ContentDeltaAsync(big);
    await coalescer.FlushAsync();

    Assert.Equal(2, received.Count); // 32K ceiling: the second 20K fragment flushes the first
    Assert.Equal(big, Assert.IsType<UiStreamEvent.Delta>(received[0]).Text);
    Assert.Equal(big, Assert.IsType<UiStreamEvent.Delta>(received[1]).Text);
  }
}
