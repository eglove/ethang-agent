using System.Collections.Specialized;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

/// <summary>Chat tool-card elapsed timing: AddToolCall stamps the call entry at
///     zero, TickToolElapsed advances the running entry, AddToolResult freezes the
///     total onto the result entry (error marker on failures), EndTurn abandons a
///     still-running tool, no-change ticks stay silent, and restored transcripts
///     carry no elapsed. Tests drive a fake seconds clock - no sleeps, exact
///     strings, deterministic.</summary>
public class ToolElapsedTranscriptTests
{
  [Fact]
  public void AddToolCall_Stamps_Zero_Elapsed_On_The_Call_Entry()
  {
    double now = 0;
    TranscriptViewModel vm = new(() => now);

    vm.AddToolCall("read", "{}");

    ToolCallEntry entry = Assert.IsType<ToolCallEntry>(vm.Entries[^1]);
    Assert.Equal("0.0s", entry.ElapsedDisplay);
  }

  [Fact]
  public void TickToolElapsed_Advances_The_Running_Entry_And_Raises_Replace()
  {
    double now = 0;
    TranscriptViewModel vm = new(() => now);
    vm.AddToolCall("read", "{}");
    List<NotifyCollectionChangedAction> changes = [];
    vm.Entries.CollectionChanged += (_, e) => changes.Add(e.Action);

    now = 0.8;
    vm.TickToolElapsed();

    ToolCallEntry entry = Assert.IsType<ToolCallEntry>(vm.Entries[^1]);
    Assert.Equal("0.8s", entry.ElapsedDisplay);
    Assert.Contains(NotifyCollectionChangedAction.Replace, changes);
  }

  [Fact]
  public void AddToolResult_Freezes_Total_Elapsed_On_The_Result_Entry()
  {
    double now = 0;
    TranscriptViewModel vm = new(() => now);
    vm.AddToolCall("read", "{}");

    now = 2.5;
    vm.AddToolResult("read", "ok", "ok", false);
    ToolResultEntry result = Assert.IsType<ToolResultEntry>(vm.Entries[^1]);
    Assert.Equal("2.5s", result.ElapsedDisplay);

    now = 9;
    vm.TickToolElapsed();

    result = Assert.IsType<ToolResultEntry>(vm.Entries[^1]);
    Assert.Equal("2.5s", result.ElapsedDisplay); // frozen against later ticks
  }

  [Fact]
  public void AddToolResult_With_Error_Appends_The_Error_Marker()
  {
    double now = 0;
    TranscriptViewModel vm = new(() => now);
    vm.AddToolCall("bash", "{}");
    now = 1.5;

    vm.AddToolResult("bash", "failed", "boom", true);

    ToolResultEntry result = Assert.IsType<ToolResultEntry>(vm.Entries[^1]);
    Assert.Equal("1.5s \u2717", result.ElapsedDisplay);
  }

  [Fact]
  public void Elapsed_At_Or_Above_A_Minute_Formats_As_m_ss()
  {
    double now = 0;
    TranscriptViewModel vm = new(() => now);
    vm.AddToolCall("exec", "{}");
    now = 125;

    vm.AddToolResult("exec", "ok", "ok", false);

    ToolResultEntry result = Assert.IsType<ToolResultEntry>(vm.Entries[^1]);
    Assert.Equal("2:05", result.ElapsedDisplay);
  }

  [Fact]
  public void A_Second_ToolCall_Starts_Fresh_Timing()
  {
    double now = 0;
    TranscriptViewModel vm = new(() => now);
    vm.AddToolCall("read", "{}");
    now = 3;
    vm.AddToolResult("read", "ok", "ok", false);

    vm.AddToolCall("bash", "{}");

    ToolCallEntry entry = Assert.IsType<ToolCallEntry>(vm.Entries[^1]);
    Assert.Equal("0.0s", entry.ElapsedDisplay);
  }

  [Fact]
  public void TickToolElapsed_Without_A_Running_Tool_Is_A_Safe_NoOp()
  {
    TranscriptViewModel vm = new(() => 0);

    vm.TickToolElapsed();

    Assert.Empty(vm.Entries);
  }

  [Fact]
  public void TickToolElapsed_With_No_Display_Change_Raises_No_Replace()
  {
    double now = 0;
    TranscriptViewModel vm = new(() => now);
    vm.AddToolCall("read", "{}");
    List<NotifyCollectionChangedAction> changes = [];
    vm.Entries.CollectionChanged += (_, e) => changes.Add(e.Action);

    vm.TickToolElapsed();
    vm.TickToolElapsed();

    Assert.DoesNotContain(NotifyCollectionChangedAction.Replace, changes);
  }

  [Fact]
  public void EndTurn_Abandons_A_Still_Running_Tool_Timing()
  {
    double now = 0;
    TranscriptViewModel vm = new(() => now);
    vm.AddToolCall("read", "{}");

    vm.EndTurn();
    now = 5;
    vm.TickToolElapsed();

    ToolCallEntry entry = Assert.IsType<ToolCallEntry>(vm.Entries[^1]);
    Assert.Equal("0.0s", entry.ElapsedDisplay);
  }

  [Fact]
  public void Restored_Transcripts_Carry_No_Elapsed_Display()
  {
    TranscriptViewModel vm = new();
    List<Message> messages =
    [
      new(Role.Assistant, "", DateTimeOffset.UtcNow,
          [new ToolCall("call_1", "read", "{}")]),
      new(Role.Tool, "contents", DateTimeOffset.UtcNow, ToolCallId: "call_1"),
    ];

    vm.Restore(messages);

    ToolCallEntry call = Assert.IsType<ToolCallEntry>(vm.Entries[0]);
    ToolResultEntry result = Assert.IsType<ToolResultEntry>(vm.Entries[1]);
    Assert.Equal("", call.ElapsedDisplay);
    Assert.Equal("", result.ElapsedDisplay);
  }
}
