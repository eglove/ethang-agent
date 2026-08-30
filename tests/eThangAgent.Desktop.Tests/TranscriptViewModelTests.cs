using System.Collections.Specialized;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

// Pure C# behavior tests — no Avalonia types. Names follow Controller Ruling R4:
// transcript entry variants are top-level records, not nested types.
public class TranscriptViewModelTests
{
  [Fact]
  public void First_Delta_Opens_Assistant_Block_Second_Extends_It()
  {
    TranscriptViewModel vm = new();
    vm.AppendAssistantDelta("Hel");
    vm.AppendAssistantDelta("lo");
    AssistantTextEntry entry = Assert.IsType<AssistantTextEntry>(vm.Entries[^1]);
    Assert.Equal("Hello", entry.Text);
    _ = Assert.Single(vm.Entries);
  }

  [Fact]
  public void Iteration_End_Closes_Block_Next_Delta_Starts_New_Entry()
  {
    TranscriptViewModel vm = new();
    vm.AppendAssistantDelta("one");
    vm.EndIteration();
    vm.AppendAssistantDelta("two");
    Assert.Equal(2, vm.Entries.Count);
    Assert.Equal("two", Assert.IsType<AssistantTextEntry>(vm.Entries[^1]).Text);
  }

  [Fact]
  public void Reasoning_Blocks_Open_Extend_And_Close_Like_Assistant()
  {
    TranscriptViewModel vm = new();
    vm.AppendReasoning("think");
    vm.AppendReasoning("ing");
    Assert.Equal("thinking", Assert.IsType<ReasoningEntry>(vm.Entries[^1]).Text);
    vm.EndIteration();
    vm.AppendReasoning("more");
    Assert.Equal(2, vm.Entries.Count);
  }

  [Fact]
  public void Non_Stream_Events_Close_Open_Blocks_First()
  {
    TranscriptViewModel vm = new();
    vm.AppendAssistantDelta("partial");
    vm.AddToolCall("read", /*lang=json,strict*/ "{\"path\":\"a.cs\"}");
    vm.AddToolResult("read", "12 lines", "1: first\n2: second", false);
    vm.AppendAssistantDelta("done");
    Assert.Equal(4, vm.Entries.Count);
    _ = Assert.IsType<ToolCallEntry>(vm.Entries[1]);
    _ = Assert.IsType<ToolResultEntry>(vm.Entries[2]);
  }

  [Fact]
  public void User_Message_And_Notice_Render_As_Their_Own_Entries()
  {
    TranscriptViewModel vm = new();
    vm.AddUser("hi");
    vm.AddNotice("Model set to test/model; applies from the next turn.");
    _ = Assert.IsType<UserMessageEntry>(vm.Entries[0]);
    _ = Assert.IsType<NoticeEntry>(vm.Entries[1]);
  }

  [Fact]
  public void Extending_An_Entry_Raises_Collection_Change_Replace()
  {
    TranscriptViewModel vm = new();
    List<NotifyCollectionChangedAction> changes = [];
    vm.Entries.CollectionChanged += (_, e) => changes.Add(e.Action);
    vm.AppendAssistantDelta("a");
    vm.AppendAssistantDelta("b");
    Assert.Contains(NotifyCollectionChangedAction.Add, changes);
    Assert.Contains(NotifyCollectionChangedAction.Replace, changes);
  }

  // ---- reasoning normalization (shared StreamedTextNormalizer) ----

  [Fact]
  public void Reasoning_NewlineFlood_IsCollapsedToOneBlankLine()
  {
    TranscriptViewModel vm = new();
    vm.AppendReasoning("step one\n\n\n\n\n\nstep two");
    Assert.Equal("step one\n\nstep two", Assert.IsType<ReasoningEntry>(vm.Entries[^1]).Text);
  }

  [Fact]
  public void Reasoning_CommaNewline_BecomesSpace()
  {
    TranscriptViewModel vm = new();
    vm.AppendReasoning("however,");
    vm.AppendReasoning("\nthe answer");
    Assert.Equal("however, the answer", Assert.IsType<ReasoningEntry>(vm.Entries[^1]).Text);
  }

  [Fact]
  public void Reasoning_SentenceBreak_IsPreserved()
  {
    TranscriptViewModel vm = new();
    vm.AppendReasoning("done.\nNext");
    Assert.Equal("done.\nNext", Assert.IsType<ReasoningEntry>(vm.Entries[^1]).Text);
  }


  // ---- empty-delta frames (providers emit "content": "" alongside reasoning) ----
  // An empty fragment is a structural no-op: it must neither break the open reasoning
  // block nor open a new one, or every streamed chunk renders as its own component.

  [Fact]
  public void Empty_Content_Delta_Does_Not_Break_Open_Reasoning_Block()
  {
    TranscriptViewModel vm = new();
    vm.AppendReasoning("I will check how");
    vm.AppendAssistantDelta("");
    vm.AppendReasoning(" the Dispatcher registers dialogs");
    ReasoningEntry entry = Assert.IsType<ReasoningEntry>(vm.Entries.Single());
    Assert.Equal("I will check how the Dispatcher registers dialogs", entry.Text);
  }

  [Fact]
  public void Empty_Reasoning_Delta_Does_Not_Open_A_New_Block()
  {
    TranscriptViewModel vm = new();
    vm.AppendReasoning("thinking");
    vm.AppendReasoning("");
    _ = Assert.Single(vm.Entries);
    Assert.Equal("thinking", Assert.IsType<ReasoningEntry>(vm.Entries[0]).Text);
  }

  [Fact]
  public void Empty_Deltas_With_No_Open_Block_Create_Nothing()
  {
    TranscriptViewModel vm = new();
    vm.AppendAssistantDelta("");
    vm.AppendReasoning("");
    Assert.Empty(vm.Entries);
  }

  // ---- restore (resume replay of a persisted transcript) ----

  [Fact]
  public void Restore_Replays_The_Persisted_Transcript_Into_Entries()
  {
    TranscriptViewModel vm = new();
    DateTimeOffset at = DateTimeOffset.UtcNow;
    vm.Restore(
    [
        new Message(Role.User, "first", at),
            new Message(Role.Assistant, "", at, [new ToolCall("call-1", "read", /*lang=json,strict*/ "{\"path\":\"a.cs\"}")]),
            new Message(Role.Tool, "file content", at, ToolCallId: "call-1"),
            new Message(Role.Assistant, "final answer", at),
            new Message(Role.System, "nudge line", at),
    ]);

    Assert.Equal(5, vm.Entries.Count);
    _ = Assert.IsType<UserMessageEntry>(vm.Entries[0]);
    ToolCallEntry call = Assert.IsType<ToolCallEntry>(vm.Entries[1]);
    Assert.Equal("read", call.Name);
    Assert.Equal(/*lang=json,strict*/ "{\"path\":\"a.cs\"}", call.Arguments);
    // The tool name resolves from the PRECEDING call batch by tool-call id; the
    // persisted content rides as the expandable FullContent, never as the summary.
    ToolResultEntry result = Assert.IsType<ToolResultEntry>(vm.Entries[2]);
    Assert.Equal("read", result.Name);
    Assert.Equal("file content", result.FullContent);
    Assert.False(result.IsError);
    Assert.Equal("final answer", Assert.IsType<AssistantTextEntry>(vm.Entries[3]).Text);
    // System messages (nudges, continuation prompts) render as notices.
    Assert.Equal("nudge line", Assert.IsType<NoticeEntry>(vm.Entries[4]).Text);
  }

  [Fact]
  public void Restore_ToolResult_Without_A_Known_Call_Falls_Back_To_Generic_Name()
  {
    TranscriptViewModel vm = new();
    vm.Restore([new Message(Role.Tool, "orphan result", DateTimeOffset.UtcNow, ToolCallId: "unknown-id")]);
    ToolResultEntry result = Assert.IsType<ToolResultEntry>(Assert.Single(vm.Entries));
    Assert.Equal("tool", result.Name);
  }

  [Fact]
  public void Restore_Assistant_Text_With_ToolCalls_Shows_Both()
  {
    TranscriptViewModel vm = new();
    DateTimeOffset at = DateTimeOffset.UtcNow;
    vm.Restore(
    [
        new Message(Role.Assistant, "checking now", at, [new ToolCall("c", "exec", "{}")]),
    ]);
    Assert.Equal(2, vm.Entries.Count);
    Assert.Equal("checking now", Assert.IsType<AssistantTextEntry>(vm.Entries[0]).Text);
    _ = Assert.IsType<ToolCallEntry>(vm.Entries[1]);
  }

  [Fact]
  public void Restore_Empty_Conversation_Adds_Nothing()
  {
    TranscriptViewModel vm = new();
    vm.Restore([]);
    Assert.Empty(vm.Entries);
  }
}
