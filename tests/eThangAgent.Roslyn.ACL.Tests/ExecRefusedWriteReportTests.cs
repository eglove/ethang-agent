using eThangAgent.CapabilityDomain;
using eThangAgent.FileSystem.ACL;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;

namespace eThangAgent.Roslyn.ACL.Tests;

/// <summary>Issue #375 end to end: a run whose file-tool call was REFUSED reports the
///     refusal and fails, even when the workspace diff is zero. The change report describes
///     effects, so a refused write and a deliberate no-op render identically there; the
///     signal is the refused call, never the zero change.</summary>
public class ExecRefusedWriteReportTests
{
  /// <summary>Script lines are joined with this instead of an escape sequence.</summary>
  private static readonly string NL = ((char)10).ToString();

  private static readonly string[] RefusedWriteScript =
  [
    @"Tools.write(new { path = ""a.txt"", content = ""replaced"" });",
    @"return ""done"";",
  ];

  private static readonly string[] PureReadScript =
  [
    @"return Tools.read(new { path = ""a.txt"", startLine = 1, endLine = 5 });",
  ];

  private static readonly string[] RefusedReadScript =
  [
    @"Tools.read(new { path = ""missing.txt"", startLine = 1, endLine = 5 });",
    @"return ""done"";",
  ];

  private static readonly string[] RefusedAndCreatedScript =
  [
    @"Tools.write(new { path = ""a.txt"", content = ""replaced"" });",
    @"Tools.write(new { path = ""b.txt"", content = ""new"" });",
    @"return ""done"";",
  ];

  private static readonly string[] RefusedThenCorrectedScript =
  [
    @"Tools.write(new { path = ""a.txt"", content = ""replaced"" });",
    @"return Tools.write(new { path = ""a.txt"", content = ""replaced"", overwrite = true });",
  ];

  private static async Task<(ExecRunResult Run, ToolResult Formatted)> RunAsync(string root, string[] script)
  {
    using DirectFileSystemAccess files = new(new EditorConfigTextConventionPolicy(root));
    AgentToolsProvider tools = new("tools",
    [
        new AgentToolBinding(new WriteTool(new WorkspacePathResolver(root), files), "Writes a file."),
        new AgentToolBinding(new ReadTool(new WorkspacePathResolver(root), files), "Reads a file."),
    ]);
    CSharpScriptExecEngine engine = new(CapabilityRegistry.Create([tools]),
        workspaceRoot: () => root, snapshots: new DirectWorkspaceSnapshotAccess());
    ExecProgram exec = new(string.Join(NL, script), TimeSpan.FromSeconds(30));
    ExecRunResult run = await engine.ExecuteAsync(exec, TestContext.Current.CancellationToken).ConfigureAwait(false);
    return (run, ExecResultFormatter.Format(run, "probe", exec));
  }

  // Acceptance 1: the only file-tool call is refused, and the program discarded its result.
  [Fact]
  public async Task RefusedWrite_WithNothingChanged_ReportsTheRefusal_AsAnError()
  {
    string root = TestTempRoot.CreateDirectory("exec-refused").FullName;
    try
    {
      await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "hello", TestContext.Current.CancellationToken);

      (ExecRunResult run, ToolResult formatted) = await RunAsync(root, RefusedWriteScript);

      Assert.NotNull(run.Changes);
      Assert.True(run.Changes.IsEmpty);
      RefusedWorkspaceCall refused = Assert.Single(run.RefusedWorkspaceCalls);
      Assert.Equal("write", refused.Tool);
      Assert.Equal("FileExists", refused.ErrorCode);
      Assert.True(formatted.IsError);
      Assert.Contains("modified 0 workspace files", formatted.Content, StringComparison.Ordinal);
      Assert.Contains("[exec: write was refused (Error [FileExists]:", formatted.Content, StringComparison.Ordinal);
      Assert.Contains("nothing was written]", formatted.Content, StringComparison.Ordinal);
      Assert.Equal("hello",
          await File.ReadAllTextAsync(Path.Combine(root, "a.txt"), TestContext.Current.CancellationToken));
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  // Acceptance 2: a program that legitimately changes nothing stays clean - the signal is
  // the refused call, not the zero change.
  [Fact]
  public async Task PureRead_ThatChangesNothing_StaysClean()
  {
    string root = TestTempRoot.CreateDirectory("exec-clean").FullName;
    try
    {
      await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "hello", TestContext.Current.CancellationToken);

      (ExecRunResult run, ToolResult formatted) = await RunAsync(root, PureReadScript);

      Assert.Empty(run.RefusedWorkspaceCalls);
      Assert.False(formatted.IsError);
      Assert.Contains("modified 0 workspace files", formatted.Content, StringComparison.Ordinal);
      Assert.DoesNotContain("was refused", formatted.Content, StringComparison.Ordinal);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  // A refused READ changes no file, so it is not a lost write: the boundary is the action's
  // own declaration, never the word 'Error' in a result.
  [Fact]
  public async Task RefusedRead_IsNotReportedAsARefusedWrite()
  {
    string root = TestTempRoot.CreateDirectory("exec-refused-read").FullName;
    try
    {
      (ExecRunResult run, ToolResult formatted) = await RunAsync(root, RefusedReadScript);

      Assert.Empty(run.RefusedWorkspaceCalls);
      Assert.False(formatted.IsError);
      Assert.DoesNotContain("was refused", formatted.Content, StringComparison.Ordinal);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  // Acceptance 3: one refusal and one success report BOTH - the change list and the refusal.
  [Fact]
  public async Task RefusedWrite_AndSuccessfulWrite_ReportBoth()
  {
    string root = TestTempRoot.CreateDirectory("exec-mixed").FullName;
    try
    {
      await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "hello", TestContext.Current.CancellationToken);

      (ExecRunResult run, ToolResult formatted) = await RunAsync(root, RefusedAndCreatedScript);

      Assert.NotNull(run.Changes);
      Assert.Equal(["b.txt"], run.Changes.Added);
      _ = Assert.Single(run.RefusedWorkspaceCalls);
      Assert.True(formatted.IsError);
      Assert.Contains("modified 1 workspace file — b.txt (added)", formatted.Content, StringComparison.Ordinal);
      Assert.Contains("[exec: write was refused (Error [FileExists]:", formatted.Content, StringComparison.Ordinal);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  // A refusal the program branched on and recovered from is still a refusal that happened:
  // the run says so, and the corrected call's effect is reported beside it.
  [Fact]
  public async Task RefusalThenCorrectedWrite_ReportsTheRefusalAndTheChange()
  {
    string root = TestTempRoot.CreateDirectory("exec-retry").FullName;
    try
    {
      await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "hello", TestContext.Current.CancellationToken);

      (ExecRunResult run, ToolResult formatted) = await RunAsync(root, RefusedThenCorrectedScript);

      Assert.NotNull(run.Changes);
      Assert.Equal(["a.txt"], run.Changes.Modified);
      _ = Assert.Single(run.RefusedWorkspaceCalls);
      Assert.True(formatted.IsError);
      Assert.Contains("modified 1 workspace file — a.txt", formatted.Content, StringComparison.Ordinal);
      Assert.Contains("[exec: write was refused", formatted.Content, StringComparison.Ordinal);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }
}
