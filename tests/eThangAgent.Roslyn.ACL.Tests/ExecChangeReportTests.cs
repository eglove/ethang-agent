using eThangAgent.CapabilityDomain;
using eThangAgent.FileSystem.ACL;
using eThangAgent.ToolDomain;

namespace eThangAgent.Roslyn.ACL.Tests;

// Issue #283 Part 1 at the engine boundary: a run reports what it changed, and a host
// that supplies no snapshot seam produces byte-identical output to before.
public class ExecChangeReportTests
{
  private static CSharpScriptExecEngine Engine(string workspace, IWorkspaceSnapshotAccess? snapshots) =>
      new(CapabilityRegistry.Create([]), workspaceRoot: () => workspace, snapshots: snapshots);

  [Fact]
  public async Task Execute_WritesOneFile_ReportsThatFile()
  {
    string root = Directory.CreateTempSubdirectory("execchg").FullName;
    try
    {
      string program = "File.WriteAllText(Path.Combine(Workspace, \"a.txt\"), \"x\"); return \"done\";";
      ExecRunResult run = await Engine(root, new DirectSnapshotAccess())
          .ExecuteAsync(new ExecProgram(program), TestContext.Current.CancellationToken);

      Assert.NotNull(run.Changes);
      Assert.Equal(["a.txt"], run.Changes.Added);
      Assert.Contains("modified 1 workspace file — a.txt (added)", run.Changes.Render(), StringComparison.Ordinal);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  // The bug from the issue, at the engine boundary.
  [Fact]
  public async Task Execute_NoOpRewrite_ReportsModifiedZero()
  {
    string root = Directory.CreateTempSubdirectory("execnoop").FullName;
    try
    {
      await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "hello", TestContext.Current.CancellationToken);
      string program = """
        var p = Path.Combine(Workspace, "a.txt");
        var t = File.ReadAllText(p);
        t = t.Replace("nope", "x");
        File.WriteAllText(p, t);
        return "done";
        """;
      ExecRunResult run = await Engine(root, new DirectSnapshotAccess())
          .ExecuteAsync(new ExecProgram(program), TestContext.Current.CancellationToken);

      Assert.NotNull(run.Changes);
      Assert.True(run.Changes.IsEmpty);
      Assert.Contains("modified 0 workspace files", run.Changes.Render(), StringComparison.Ordinal);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  [Fact]
  public async Task Execute_ReadOnly_ReportsNothingChanged()
  {
    string root = Directory.CreateTempSubdirectory("execread").FullName;
    try
    {
      await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "hello", TestContext.Current.CancellationToken);
      ExecRunResult run = await Engine(root, new DirectSnapshotAccess())
          .ExecuteAsync(new ExecProgram("return File.ReadAllText(Path.Combine(Workspace, \"a.txt\"));"),
              TestContext.Current.CancellationToken);

      Assert.NotNull(run.Changes);
      Assert.True(run.Changes.IsEmpty);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  // A host with no snapshot seam keeps its exact legacy contract: no report at all,
  // never a fabricated 'changed nothing'.
  [Fact]
  public async Task Execute_NoSnapshotSeam_ReportsNoChanges()
  {
    ExecRunResult run = await Engine("C:\\ws", snapshots: null)
        .ExecuteAsync(new ExecProgram("return 1;"), TestContext.Current.CancellationToken);

    Assert.Null(run.Changes);
  }

  // A snapshot fault must never fail the run that produced it.
  [Fact]
  public async Task Execute_SnapshotFault_DoesNotFailTheRun()
  {
    ExecRunResult run = await Engine("C:\\ws", new ThrowingSnapshotAccess())
        .ExecuteAsync(new ExecProgram("return 7;"), TestContext.Current.CancellationToken);

    Assert.Equal(ExecRunStatus.Completed, run.Status);
    Assert.Equal("7", run.Output);
    Assert.Null(run.Changes);
  }

  private sealed class DirectSnapshotAccess : IWorkspaceSnapshotAccess
  {
    public Task<WorkspaceSnapshot> CaptureAsync(string workspaceRoot, CancellationToken ct = default) =>
        new DirectWorkspaceSnapshotAccess().CaptureAsync(workspaceRoot, ct);
  }

  private sealed class ThrowingSnapshotAccess : IWorkspaceSnapshotAccess
  {
    public Task<WorkspaceSnapshot> CaptureAsync(string workspaceRoot, CancellationToken ct = default) =>
        throw new InvalidOperationException("snapshot unavailable");
  }
}
