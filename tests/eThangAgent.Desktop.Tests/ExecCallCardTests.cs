using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

/// <summary>Grand-plan tool-output contract, call-card half: an exec call card's
///     header is [gear][title] with the right slot showing elapsed/budget (1.2s / 120s),
///     and its body is ONLY the program text fenced as C# for the markdown renderer —
///     never the raw JSON arguments. Non-exec cards keep today's shape exactly.
///     Restored cards derive the same view from the stored args JSON.</summary>
public class ExecCallCardTests
{
  private const string ExecArgs = /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"title\":\"parse names\",\"program\":\"return 42;\"}";

  [Fact]
  public void ExecCall_HeaderTitle_ComesFromTheRequiredTitleInput()
  {
    TranscriptViewModel vm = new();
    vm.AddToolCall("exec", ExecArgs);

    ToolCallEntry entry = Assert.IsType<ToolCallEntry>(Assert.Single(vm.Entries));
    Assert.True(entry.IsExecCall);
    Assert.Equal("parse names", entry.HeaderTitle);
  }

  [Fact]
  public void ExecCall_ProgramBody_IsOnlyTheFencedProgram_NeverTheJson()
  {
    TranscriptViewModel vm = new();
    vm.AddToolCall("exec", ExecArgs);

    ToolCallEntry entry = Assert.IsType<ToolCallEntry>(Assert.Single(vm.Entries));
    Assert.Equal("```csharp\nreturn 42;\n```", entry.ProgramBody);
  }

  [Fact]
  public void ExecCall_RightSlot_ShowsElapsedOverBudget_WhileRunning()
  {
    TranscriptViewModel vm = new();
    vm.AddToolCall("exec", ExecArgs);

    ToolCallEntry entry = Assert.IsType<ToolCallEntry>(Assert.Single(vm.Entries));
    Assert.StartsWith(entry.ElapsedDisplay, entry.HeaderTimeDisplay, StringComparison.Ordinal);
    Assert.EndsWith(" / 120s", entry.HeaderTimeDisplay, StringComparison.Ordinal);
  }

  [Fact]
  public void RestoredExecCall_NoHandle_RightSlot_ShowsBudgetOnly_AndStillRendersTitleAndProgram()
  {
    TranscriptViewModel vm = new();
    vm.Restore(
    [
      new Message(Role.Assistant, "",
          DateTimeOffset.UtcNow,
          [new ToolCall("c1", "exec", ExecArgs)]),
    ]);

    ToolCallEntry entry = Assert.IsType<ToolCallEntry>(Assert.Single(vm.Entries));
    Assert.Equal("", entry.ElapsedDisplay);
    Assert.Equal("120s", entry.HeaderTimeDisplay);
    Assert.Equal("parse names", entry.HeaderTitle);
    Assert.Equal("```csharp\nreturn 42;\n```", entry.ProgramBody);
  }

  [Fact]
  public void NonExecCall_KeepsLegacyShape_PreviewAndJsonBody_Unchanged()
  {
    TranscriptViewModel vm = new();
    vm.AddToolCall("read", /*lang=json,strict*/ "{\"path\":\"a.cs\"}");

    ToolCallEntry entry = Assert.IsType<ToolCallEntry>(Assert.Single(vm.Entries));
    Assert.False(entry.IsExecCall);
    Assert.Null(entry.ProgramBody);
    Assert.Contains("\"path\"", entry.ArgumentsFormatted, StringComparison.Ordinal);
    Assert.Equal(entry.ElapsedDisplay, entry.HeaderTimeDisplay);
  }

  [Fact]
  public void ExecCall_WithoutTitle_LegacyArgs_FallsBackToToolName()
  {
    TranscriptViewModel vm = new();
    vm.AddToolCall("exec", /*lang=json,strict*/ "{\"timeoutSeconds\":60,\"program\":\"return 1;\"}");

    ToolCallEntry entry = Assert.IsType<ToolCallEntry>(Assert.Single(vm.Entries));
    Assert.True(entry.IsExecCall);
    Assert.Equal("exec", entry.HeaderTitle);
  }

  [Fact]
  public void ExecCall_MalformedArgs_FallsBackToLegacyRendering()
  {
    TranscriptViewModel vm = new();
    vm.AddToolCall("exec", "not json at all");

    ToolCallEntry entry = Assert.IsType<ToolCallEntry>(Assert.Single(vm.Entries));
    Assert.False(entry.IsExecCall);
    Assert.Null(entry.ProgramBody);
  }
}
