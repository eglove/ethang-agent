using System.Collections.Specialized;
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
    vm.AddToolResult("read", "12 lines");
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
    vm.AddNotice("Commands:/help");
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
}
