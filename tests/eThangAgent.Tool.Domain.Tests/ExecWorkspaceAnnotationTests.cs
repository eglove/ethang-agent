using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Tool.Domain.Tests;

// ---- workspace vs launch-directory guard (session 4ebb01ae retrospective) ----
// A script that touches Directory.GetCurrentDirectory() gets the APP's launch
// directory, not the session workspace. The formatter must surface both paths so
// the model sees the mismatch instead of silently reading the wrong directory.
public class ExecWorkspaceAnnotationTests
{
  private static readonly ExecOptions Options = ExecOptions.Default;

  private static ExecProgram Program(string text)
  {
    Result<ExecProgram> created = ExecProgram.Create(text, Options);
    Assert.True(created.IsSuccess);
    return created.Value;
  }

  [Fact]
  public void Completed_ProgramTouchedCurrentDirectory_AppendsWorkspaceAnnotation()
  {
    ExecRunResult run = new(ExecRunStatus.Completed, "done", [],
        WorkspaceRoot: "C:\\ws", LaunchDirectory: "C:\\app");

    ToolResult result = ExecResultFormatter.Format(run, Options, null, null, Program("var x = Directory.GetCurrentDirectory();"));

    Assert.False(result.IsError);
    Assert.Contains("[exec: workspace", result.Content, StringComparison.Ordinal);
    Assert.Contains("C:\\ws", result.Content, StringComparison.Ordinal);
    Assert.Contains("C:\\app", result.Content, StringComparison.Ordinal);
    Assert.Contains("Directory.GetCurrentDirectory()", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Completed_ProgramDidNotTouchCurrentDirectory_NoAnnotation()
  {
    ExecRunResult run = new(ExecRunStatus.Completed, "done", [],
        WorkspaceRoot: "C:\\ws", LaunchDirectory: "C:\\app");

    ToolResult result = ExecResultFormatter.Format(run, Options, null, null, Program("return 1;"));

    Assert.DoesNotContain("[exec: workspace", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Completed_MissingWorkspaceSnapshot_NoAnnotation()
  {
    // Legacy engines / fakes that do not supply the snapshot: no annotation,
    // byte-identical legacy behavior (the AgentOptions leniency pattern).
    ExecRunResult run = new(ExecRunStatus.Completed, "done", []);

    ToolResult result = ExecResultFormatter.Format(run, Options, null, null, Program("Directory.GetCurrentDirectory();"));

    Assert.DoesNotContain("[exec: workspace", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Completed_SamePaths_NoAnnotation()
  {
    // When the launch directory IS the workspace (headless/test hosts), the
    // distinction is moot — no annotation.
    ExecRunResult run = new(ExecRunStatus.Completed, "done", [],
        WorkspaceRoot: "C:\\same", LaunchDirectory: "C:\\same");

    ToolResult result = ExecResultFormatter.Format(run, Options, null, null, Program("Directory.GetCurrentDirectory();"));

    Assert.DoesNotContain("[exec: workspace", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Completed_TitledCall_StillAnnotates()
  {
    // The production ExecTool path passes title + program; annotation must survive.
    ExecRunResult run = new(ExecRunStatus.Completed, "done", [],
        WorkspaceRoot: "C:\\ws", LaunchDirectory: "C:\\app");

    ToolResult result = ExecResultFormatter.Format(run, Options, null, "probe workspace", Program("Directory.GetCurrentDirectory();"));

    Assert.Contains("[exec: workspace", result.Content, StringComparison.Ordinal);
  }
}
