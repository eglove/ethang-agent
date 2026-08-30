using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

public class ToolResultTranscriptTests
{
  [Fact]
  public void AddToolResult_Stores_FullContent_And_ErrorFlag()
  {
    TranscriptViewModel vm = new();
    vm.AddToolResult("exec", "Error [ScriptError]: boom", "Error [ScriptError]: boom", true);
    ToolResultEntry entry = Assert.IsType<ToolResultEntry>(Assert.Single(vm.Entries));
    Assert.Equal("exec", entry.Name);
    Assert.Equal("Error [ScriptError]: boom", entry.FullContent);
    Assert.True(entry.IsError);
  }

  [Fact]
  public void Restore_ToolResult_Carries_Persisted_Content_As_FullContent()
  {
    TranscriptViewModel vm = new();
    List<Message> messages =
    [
      new(Role.Assistant, "", DateTimeOffset.UtcNow,
          [new ToolCall("call_1", "read", "{}")]),
      new(Role.Tool, "file contents here", DateTimeOffset.UtcNow,
          ToolCallId: "call_1"),
    ];

    vm.Restore(messages);

    ToolResultEntry entry = Assert.IsType<ToolResultEntry>(vm.Entries[^1]);
    Assert.Equal("read", entry.Name);
    Assert.Equal("file contents here", entry.FullContent);
    Assert.False(entry.IsError); // transcripts persist no error flag
  }

  [Fact]
  public void CloseAssistantBlock_Flips_IsOpen_For_Markdown_Rendering()
  {
    TranscriptViewModel vm = new();
    vm.AppendAssistantDelta("hello **world**");
    AssistantTextEntry before = Assert.IsType<AssistantTextEntry>(vm.Entries[^1]);
    Assert.True(before.IsOpen);

    vm.EndIteration();

    AssistantTextEntry after = Assert.IsType<AssistantTextEntry>(vm.Entries[^1]);
    Assert.False(after.IsOpen);
  }

  [Fact]
  public void Assistant_Block_Closes_When_A_Different_Stream_Kind_Resumes()
  {
    TranscriptViewModel vm = new();
    vm.AppendReasoning("thought");
    vm.AppendAssistantDelta("answer");
    vm.AppendReasoning("more");
    // Resuming reasoning closes the text block - it is markdown-ready from then on.
    AssistantTextEntry entry = Assert.IsType<AssistantTextEntry>(vm.Entries[1]);
    Assert.False(entry.IsOpen);
    Assert.Equal(3, vm.Entries.Count);
  }
}
