using eThangAgent.ToolDomain;

namespace eThangAgent.Tool.Domain.Tests;

public class ExecResultFormatterTests
{
    private static readonly ExecOptions Options = ExecOptions.Default;

    [Fact]
    public void Completed_UnderCap_PassesOutputThrough_NotAnError()
    {
        var result = ExecResultFormatter.Format(
            ExecRunResult.Completed("hello\nworld"), Options, null);

        Assert.False(result.IsError);
        Assert.Equal("hello\nworld", result.Content);
    }

    [Fact]
    public void Completed_WithArtifactPath_AppendsArtifactLine()
    {
        var result = ExecResultFormatter.Format(
            ExecRunResult.Completed("small"), Options, "C:\\tmp\\a.txt");

        Assert.Contains("[exec:artifact C:\\tmp\\a.txt]", result.Content);
    }

    [Fact]
    public void Completed_OverCap_KeepsBothEnds_WithMarkersAndArtifact()
    {
        var options = new ExecOptions { MaxOutputChars = 20 };
        var run = ExecRunResult.Completed("0123456789abcdefghijklmnopqrstuvwxyz"); // 36 chars

        var result = ExecResultFormatter.Format(run, options, "C:\\a.txt");

        Assert.False(result.IsError);
        Assert.StartsWith("0123456789", result.Content);
        Assert.EndsWith("uvwxyz", result.Content);
        Assert.Contains("[exec: output truncated", result.Content);
        Assert.Contains("[exec:artifact C:\\a.txt]", result.Content);
    }

    [Fact]
    public void Completed_WithErrorLines_IsError_WithScriptErrorGutters()
    {
        var run = new ExecRunResult(ExecRunStatus.Completed, "partial", ["boom"], null);

        var result = ExecResultFormatter.Format(run, Options, null);

        Assert.True(result.IsError);
        Assert.Contains("exec error [ScriptError]: boom", result.Content);
        Assert.Contains("partial", result.Content);
    }

    [Fact]
    public void Timeout_IsError_WithGutterAndBoundedPartialOutput()
    {
        var run = new ExecRunResult(ExecRunStatus.Timeout, "some output", [],
            "Execution timed out after 120s.");

        var result = ExecResultFormatter.Format(run, Options, null);

        Assert.True(result.IsError);
        Assert.Contains("exec error [ExecTimeout]: Execution timed out after 120s.", result.Content);
        Assert.Contains("some output", result.Content);
    }

    [Fact]
    public void Cancelled_IsError_ExecCancelled()
    {
        var run = new ExecRunResult(ExecRunStatus.Cancelled, "", [], "Execution cancelled.");

        var result = ExecResultFormatter.Format(run, Options, null);

        Assert.True(result.IsError);
        Assert.Contains("exec error [ExecCancelled]:", result.Content);
    }

    [Fact]
    public void EngineFailure_IsError_ExecEngineFailure()
    {
        var run = new ExecRunResult(ExecRunStatus.EngineFailure, "", [], "runspace died");

        var result = ExecResultFormatter.Format(run, Options, null);

        Assert.True(result.IsError);
        Assert.Contains("exec error [ExecEngineFailure]: runspace died", result.Content);
    }

    [Fact]
    public void Timeout_PartialOutput_IsBoundedToErrorCeiling()
    {
        var options = new ExecOptions { MaxErrorChars = 10 };
        var run = new ExecRunResult(ExecRunStatus.Timeout, "abcdefghijklmnopqrstuvwxyz", [], "too slow");

        var result = ExecResultFormatter.Format(run, options, null);

        Assert.Contains("[exec: partial output truncated", result.Content);
        Assert.DoesNotContain("mnopqrstuvwxyz", result.Content);
    }

    [Fact]
    public void ParseErrors_BoundedToMax_WithHiddenCount()
    {
        var errors = Enumerable.Range(1, 15)
            .Select(i => new ExecParseError(i, 1, $"error {i}"))
            .ToList();

        var result = ExecResultFormatter.ParseErrors(errors, 10);

        Assert.True(result.IsError);
        Assert.Contains("exec error [ExecParseError]:", result.Content);
        Assert.Contains("line 10, col 1: error 10", result.Content);
        Assert.DoesNotContain("line 11, col 1", result.Content);
        Assert.Contains("[5 more parse error(s) not shown]", result.Content);
    }
}
