using eThangAgent.ToolDomain;

namespace eThangAgent.Tool.Domain.Tests;

public class ExecResultFormatterTests
{
  [Fact]
  public void Completed_PassesOutputThrough_NotAnError()
  {
    ToolResult result = ExecResultFormatter.Format(ExecRunResult.Completed("hello\nworld"));

    Assert.False(result.IsError);
    Assert.Equal("hello\nworld", result.Content);
  }

  [Fact]
  public void Completed_OversizedOutput_PassesThroughInFull()
  {
    string output = new('x', 60 * 1024);
    ToolResult result = ExecResultFormatter.Format(ExecRunResult.Completed(output));

    Assert.False(result.IsError);
    Assert.Equal(60 * 1024, result.Content.Length);
  }

  [Fact]
  public void Completed_WithErrorLines_IsError_WithScriptErrorGutters()
  {
    ExecRunResult run = new(ExecRunStatus.Completed, "partial", ["boom"], null);

    ToolResult result = ExecResultFormatter.Format(run);

    Assert.True(result.IsError);
    Assert.Contains("exec error [ScriptError]: boom", result.Content, StringComparison.Ordinal);
    Assert.Contains("partial", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Completed_WithErrorLines_DoesNotDoubleWrapAlreadyTaggedLine()
  {
    // The engine hands over ScriptToolException messages that already carry their
    // Error [CODE] tag; the formatter's gutter must not stack a second one.
    ExecRunResult run = new(ExecRunStatus.Completed, "",
        ["Error [MissingParameter]: nested call 'read': Missing required parameter 'timeoutSeconds'."], null);

    ToolResult result = ExecResultFormatter.Format(run);

    Assert.True(result.IsError);
    Assert.Contains("exec error [ScriptError]: Error [MissingParameter]: nested call 'read':",
        result.Content, StringComparison.Ordinal);
    Assert.Equal(1, CountOccurrences(result.Content, "[ScriptError]"));
  }

  [Fact]
  public void Completed_WithErrorLinesAndEmptyOutput_DoesNotStartWithNewline()
  {
    ExecRunResult run = new(ExecRunStatus.Completed, "", ["Error [MissingParameter]: boom"], null);

    ToolResult result = ExecResultFormatter.Format(run);

    Assert.True(result.IsError);
    Assert.StartsWith("exec error [ScriptError]:", result.Content, StringComparison.Ordinal);
  }



  private static int CountOccurrences(string text, string needle)
  {
    int count = 0;
    int index = 0;
    while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
    {
      count++;
      index += needle.Length;
    }

    return count;
  }

  [Fact]
  public void Timeout_IsError_WithGutterAndBoundedPartialOutput()
  {
    ExecRunResult run = new(ExecRunStatus.Timeout, "some output", [],
        "Execution timed out after 120s.");

    ToolResult result = ExecResultFormatter.Format(run);

    Assert.True(result.IsError);
    Assert.Contains("exec error [ExecTimeout]: Execution timed out after 120s.", result.Content, StringComparison.Ordinal);
    Assert.Contains("some output", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Cancelled_IsError_ExecCancelled()
  {
    ExecRunResult run = new(ExecRunStatus.Cancelled, "", [], "Execution cancelled.");

    ToolResult result = ExecResultFormatter.Format(run);

    Assert.True(result.IsError);
    Assert.Contains("exec error [ExecCancelled]:", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void EngineFailure_IsError_ExecEngineFailure()
  {
    ExecRunResult run = new(ExecRunStatus.EngineFailure, "", [], "runspace died");

    ToolResult result = ExecResultFormatter.Format(run);

    Assert.True(result.IsError);
    Assert.Contains("exec error [ExecEngineFailure]: runspace died", result.Content, StringComparison.Ordinal);
  }


  [Fact]
  public void ParseErrors_With_Hints_Renders_Hints_Above_Diagnostics()
  {
    List<ExecParseError> errors = [new(1, 1, "'; expected")];
    ToolResult result = ExecResultFormatter.ParseErrors(errors, ["hint one", "hint two"]);

    Assert.True(result.IsError);
    Assert.True(result.Content.IndexOf("hint one", StringComparison.Ordinal)
        < result.Content.IndexOf("line 1, col 1", StringComparison.Ordinal));
    Assert.Contains("hint two", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void ParseErrors_ListsEveryDiagnostic()
  {
    List<ExecParseError> errors = [.. Enumerable.Range(1, 15).Select(i => new ExecParseError(i, 1, $"error {i}"))];

    ToolResult result = ExecResultFormatter.ParseErrors(errors);

    Assert.True(result.IsError);
    Assert.Contains("exec error [ExecParseError]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("line 10, col 1: error 10", result.Content, StringComparison.Ordinal);
    Assert.Contains("line 15, col 1: error 15", result.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("not shown", result.Content, StringComparison.Ordinal);
  }

  // ---- empty-output hint: a discarded nested-call result (session 851d...) ----

  [Fact]
  public void Completed_EmptyOutput_WithNestedDispatches_HintsDiscardedResult()
  {
    ExecRunResult run = new(ExecRunStatus.Completed, "", [], null, NestedDispatchCount: 1);

    ToolResult result = ExecResultFormatter.Format(run);

    Assert.False(result.IsError);
    Assert.Contains("[exec: empty output", result.Content, StringComparison.Ordinal);
    Assert.Contains("discarded", result.Content, StringComparison.Ordinal);
    Assert.Contains("var r = Tools.Invoke(...)", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Completed_EmptyOutput_NoNestedDispatches_StaysSilent()
  {
    ToolResult result = ExecResultFormatter.Format(ExecRunResult.Completed(""));

    Assert.False(result.IsError);
    Assert.Equal("", result.Content);
  }

  [Fact]
  public void Completed_NonEmptyOutput_WithNestedDispatches_NeverHints()
  {
    ExecRunResult run = new(ExecRunStatus.Completed, "ok", [], null, NestedDispatchCount: 3);

    ToolResult result = ExecResultFormatter.Format(run);

    Assert.Equal("ok", result.Content);
  }
}
