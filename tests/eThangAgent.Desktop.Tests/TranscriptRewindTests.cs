using eThangAgent.Desktop.Streaming;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

/// <summary>The rewind contract, in both pieces it spans: the transcript withdraws exactly
///  the open block and nothing else, and the coalescer delivers buffered text BEFORE the
///  withdrawal reaches the sink, so what the sink is asked to retract is what it was shown.</summary>
public class TranscriptRewindTests
{
  private sealed class RecordingSink : IStreamSink
  {
    public List<UiStreamEvent> Received { get; } = [];

    public Task DeliverAsync(UiStreamEvent evt)
    {
      Received.Add(evt);
      return Task.CompletedTask;
    }
  }

  /// <summary>Names the production change that would fail this test: keeping the open entry
  ///     would leave the failed attempt's text on screen above the retry's own answer.</summary>
  [Fact]
  public void Rewind_Removes_The_Open_Assistant_Block()
  {
    TranscriptViewModel vm = new();
    vm.AddUser("hi");
    vm.AppendAssistantDelta("Hello, wor");

    vm.RewindStreamedText();

    _ = Assert.Single(vm.Entries);
    _ = Assert.IsType<UserMessageEntry>(vm.Entries[0]);
    Assert.Equal(string.Empty, vm.AssistantText, StringComparer.Ordinal);
  }

  [Fact]
  public void Rewind_Removes_The_Open_Reasoning_Block()
  {
    TranscriptViewModel vm = new();
    vm.AppendReasoning("thinking");

    vm.RewindStreamedText();

    Assert.Empty(vm.Entries);
  }

  /// <summary>The retry streams into a FRESH block: without resetting the open index the next
  ///     delta would extend the entry just removed.</summary>
  [Fact]
  public void Retry_Text_Opens_A_Fresh_Block_After_A_Rewind()
  {
    TranscriptViewModel vm = new();
    vm.AppendAssistantDelta("Hello, wor");
    vm.RewindStreamedText();

    vm.AppendAssistantDelta("Hi there.");

    AssistantTextEntry entry = Assert.IsType<AssistantTextEntry>(Assert.Single(vm.Entries));
    Assert.Equal("Hi there.", entry.Text, StringComparer.Ordinal);
  }

  /// <summary>Committed work survives: a tool card and a notice are not open entries.</summary>
  [Fact]
  public void Rewind_Leaves_Committed_Tool_Cards_And_Notices_Alone()
  {
    TranscriptViewModel vm = new();
    vm.AddToolCall("read", "args");
    vm.AddToolResult("read", "ok", "1: first", isError: false);
    vm.AddNotice("Model selected: test/model");
    vm.AppendAssistantDelta("partial");

    vm.RewindStreamedText();

    Assert.Equal(3, vm.Entries.Count);
    _ = Assert.IsType<ToolCallEntry>(vm.Entries[0]);
    _ = Assert.IsType<ToolResultEntry>(vm.Entries[1]);
    _ = Assert.IsType<NoticeEntry>(vm.Entries[2]);
  }

  /// <summary>An earlier turn's finished answer is closed, so a rewind in a later turn cannot
  ///     reach it.</summary>
  [Fact]
  public void Rewind_Leaves_An_Earlier_Turns_Completed_Answer_Alone()
  {
    TranscriptViewModel vm = new();
    vm.AppendAssistantDelta("first answer");
    vm.EndIteration();
    vm.AddUser("again");
    vm.AppendAssistantDelta("second attempt");

    vm.RewindStreamedText();

    Assert.Equal("first answer", vm.AssistantText, StringComparer.Ordinal);
    Assert.Equal(2, vm.Entries.Count); // the first answer and the new user message
    _ = Assert.IsType<UserMessageEntry>(vm.Entries[1]);
  }

  /// <summary>A rewind with nothing open removes nothing, and a second rewind is harmless.</summary>
  [Fact]
  public void Rewind_With_No_Open_Block_Is_A_No_Op()
  {
    TranscriptViewModel vm = new();
    vm.AddUser("hi");

    vm.RewindStreamedText();
    vm.RewindStreamedText();

    _ = Assert.Single(vm.Entries);
  }

  /// <summary>THE ORDER IS THE CONTRACT: text the coalescer still holds is delivered first,
  ///     so the sink retracts what it was actually shown and nothing arrives after it.</summary>
  [Fact]
  public async Task Coalescer_Delivers_Buffered_Text_Before_The_Rewind()
  {
    RecordingSink sink = new();
    StreamWindowCoalescer coalescer = new(sink, windowSeconds: 0.08, () => 0.0);

    await coalescer.ContentDeltaAsync("Hello, wor");
    await coalescer.RewindAsync();
    await coalescer.NoticeAsync("retrying in 1s (attempt 2 of 6)");
    await coalescer.FlushAsync();

    Assert.Equal(3, sink.Received.Count);
    Assert.Equal("Hello, wor", Assert.IsType<UiStreamEvent.Delta>(sink.Received[0]).Text, StringComparer.Ordinal);
    _ = Assert.IsType<UiStreamEvent.Rewind>(sink.Received[1]);
    _ = Assert.IsType<UiStreamEvent.Notice>(sink.Received[2]);
  }

  /// <summary>End to end through the bridge and the transcript: one answer, shown once.</summary>
  [Fact]
  public async Task Bridge_And_Transcript_Show_The_Retry_Answer_Once()
  {
    TranscriptViewModel transcript = new();
    StreamBridge bridge = new(evt =>
    {
      switch (evt)
      {
        case UiStreamEvent.Delta d:
          transcript.AppendAssistantDelta(d.Text);
          break;
        case UiStreamEvent.Rewind:
          transcript.RewindStreamedText();
          break;
        case UiStreamEvent.Notice n:
          transcript.AddNotice(n.Text);
          break;
        default:
          break;
      }

      return Task.CompletedTask;
    }, coalesce: true);
    bridge.Start();

    bridge.OnContentDelta("Hello, wor");
    bridge.OnStreamRewind();
    bridge.OnNotice("retrying in 1s (attempt 2 of 6)");
    bridge.OnContentDelta("Hi there.");
    bridge.MarkTurnComplete();
    await bridge.DrainUntilIdleAsync();

    Assert.Equal("Hi there.", transcript.AssistantText, StringComparer.Ordinal);
    _ = Assert.Single(transcript.Notices);
  }
}
