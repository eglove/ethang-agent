using eThangAgent.CapabilityDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Roslyn.ACL.Tests;

// The engine stamps the workspace snapshot onto every run result so the
// formatter can surface the workspace-vs-launch-directory distinction.
public class ExecWorkspaceSnapshotTests
{
  [Fact]
  public async Task Execute_StampsWorkspaceAndLaunchDirectory()
  {
    CSharpScriptExecEngine engine = new(CapabilityRegistry.Create([]),
        workspaceRoot: () => "C:\\ws");

    ExecRunResult run = await engine.ExecuteAsync(new ExecProgram("return 1;"),
        ct: TestContext.Current.CancellationToken);

    Assert.Equal(ExecRunStatus.Completed, run.Status);
    Assert.Equal("C:\\ws", run.WorkspaceRoot);
    Assert.Equal(Directory.GetCurrentDirectory(), run.LaunchDirectory);
  }

  [Fact]
  public async Task Execute_ScriptFault_StillStampsSnapshot()
  {
    CSharpScriptExecEngine engine = new(CapabilityRegistry.Create([]),
        workspaceRoot: () => "C:\\ws");

    ExecRunResult run = await engine.ExecuteAsync(
        new ExecProgram("throw new System.Exception(\"boom\");"),
        ct: TestContext.Current.CancellationToken);

    Assert.NotEmpty(run.ErrorLines);
    Assert.Equal("C:\\ws", run.WorkspaceRoot);
    Assert.Equal(Directory.GetCurrentDirectory(), run.LaunchDirectory);
  }
}
