using eThangAgent.ToolDomain;

namespace eThangAgent.Tool.Domain.Tests;

public class ExecResultFormatterTests
{
  private static readonly ExecOptions Options = ExecOptions.Default;

  [Fact]
  public void Completed_UnderCap_PassesOutputThrough_NotAnError()
  {
    ToolResult result = ExecResultFormatter.Format(
            ExecRunResult.Completed("hello\nworld"), Options, null);

    Assert.False(result.IsError);
    Assert.Equal("hello\nworld", result.Content);
  }

  [Fact]
  public void Completed_WithArtifactPath_AppendsArtifactLine()
  {
    ToolResult result = ExecResultFormatter.Format(
            ExecRunResult.Completed("small"), Options, "C:\\tmp\\a.txt");

    Assert.Contains("[exec:artifact C:\\tmp\\a.txt]", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Completed_OverCap_KeepsBothEnds_WithMarkersAndArtifact()
  {
    ExecOptions options = new() { MaxOutputChars = 20 };
    ExecRunResult run = ExecRunResult.Completed("0123456789abcdefghijklmnopqrstuvwxyz"); // 36 chars

    ToolResult result = ExecResultFormatter.Format(run, options, "C:\\a.txt");

    Assert.False(result.IsError);
    Assert.StartsWith("0123456789", result.Content, StringComparison.Ordinal);
    Assert.EndsWith("uvwxyz", result.Content, StringComparison.Ordinal);
    Assert.Contains("[exec: output truncated", result.Content, StringComparison.Ordinal);
    Assert.Contains("[exec:artifact C:\\a.txt]", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Completed_WithErrorLines_IsError_WithScriptErrorGutters()
  {
    ExecRunResult run = new(ExecRunStatus.Completed, "partial", ["boom"], null);

    ToolResult result = ExecResultFormatter.Format(run, Options, null);

    Assert.True(result.IsError);
    Assert.Contains("exec error [ScriptError]: boom", result.Content, StringComparison.Ordinal);
    Assert.Contains("partial", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Timeout_IsError_WithGutterAndBoundedPartialOutput()
  {
    ExecRunResult run = new(ExecRunStatus.Timeout, "some output", [],
        "Execution timed out after 120s.");

    ToolResult result = ExecResultFormatter.Format(run, Options, null);

    Assert.True(result.IsError);
    Assert.Contains("exec error [ExecTimeout]: Execution timed out after 120s.", result.Content, StringComparison.Ordinal);
    Assert.Contains("some output", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Cancelled_IsError_ExecCancelled()
  {
    ExecRunResult run = new(ExecRunStatus.Cancelled, "", [], "Execution cancelled.");

    ToolResult result = ExecResultFormatter.Format(run, Options, null);

    Assert.True(result.IsError);
    Assert.Contains("exec error [ExecCancelled]:", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void EngineFailure_IsError_ExecEngineFailure()
  {
    ExecRunResult run = new(ExecRunStatus.EngineFailure, "", [], "runspace died");

    ToolResult result = ExecResultFormatter.Format(run, Options, null);

    Assert.True(result.IsError);
    Assert.Contains("exec error [ExecEngineFailure]: runspace died", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Timeout_PartialOutput_IsBoundedToErrorCeiling()
  {
    ExecOptions options = new() { MaxErrorChars = 10 };
    ExecRunResult run = new(ExecRunStatus.Timeout, "abcdefghijklmnopqrstuvwxyz", [], "too slow");

    ToolResult result = ExecResultFormatter.Format(run, options, null);

    Assert.Contains("[exec: partial output truncated", result.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("mnopqrstuvwxyz", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void ParseErrors_BoundedToMax_WithHiddenCount()
  {
    List<ExecParseError> errors = [.. Enumerable.Range(1, 15).Select(i => new ExecParseError(i, 1, $"error {i}"))];

    ToolResult result = ExecResultFormatter.ParseErrors(errors, 10);

    Assert.True(result.IsError);
    Assert.Contains("exec error [ExecParseError]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("line 10, col 1: error 10", result.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("line 11, col 1", result.Content, StringComparison.Ordinal);
    Assert.Contains("[5 more parse error(s) not shown]", result.Content, StringComparison.Ordinal);
  }
}
