using eThangAgent.CapabilityDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Roslyn.ACL.Tests;

/// <summary>The relative-path leak (2026-10-22): a session rooted at personal-vault
///     wrote p1.txt and doc161.pdf into the ethang-agent repo, because raw
///     System.IO relative paths inside an exec script resolve against the PROCESS CWD
///     (the app's launch directory) and not the session workspace. The fix is a
///     workspace-rooted path resolver exposed to scripts as a first-class global, so a
///     relative path resolves at the session workspace (or the anchored child's
///     anchor) WITHOUT touching process-global state - the engine runs scripts
///     in-process and concurrent sessions share the process.</summary>
public class ExecWorkspacePathTests
{
  private static CSharpScriptExecEngine CreateEngine(string workspace)
      => new(CapabilityRegistry.Create([]), workspaceRoot: () => workspace);

  [Fact]
  public async Task Resolve_RelativePath_ResolvesAtTheWorkspace()
  {
    string workspace = Directory.CreateTempSubdirectory("ethang-exec-ws").FullName;
    try
    {
      CSharpScriptExecEngine engine = CreateEngine(workspace);

      ExecRunResult run = await engine.ExecuteAsync(
          new ExecProgram("return ResolvePath(\"notes.txt\");"),
          ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

      Assert.Equal(ExecRunStatus.Completed, run.Status);
      string resolved = run.Output.Trim();
      Assert.Equal(Path.Combine(workspace, "notes.txt"), resolved, StringComparer.OrdinalIgnoreCase);
    }
    finally
    {
      Directory.Delete(workspace, recursive: true);
    }
  }

  [Fact]
  public async Task Resolve_PathThatEscapesTheWorkspace_IsRefused()
  {
    string workspace = Directory.CreateTempSubdirectory("ethang-exec-ws").FullName;
    try
    {
      CSharpScriptExecEngine engine = CreateEngine(workspace);

      // A '..' escape is REFUSED, not silently re-rooted: the resolver's contract is
      // the file tools' contract, and the refusal is what keeps a write inside the
      // session's own workspace.
      ExecRunResult run = await engine.ExecuteAsync(
          new ExecProgram("return ResolvePath(\"..\\\\escape.txt\");"),
          ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

      Assert.Equal(ExecRunStatus.Completed, run.Status);
      // The refusal THROWS inside the script, so the message lands in the error lines.
      Assert.Contains(run.ErrorLines, line => line.Contains("outside the workspace", StringComparison.OrdinalIgnoreCase));
    }
    finally
    {
      Directory.Delete(workspace, recursive: true);
    }
  }

  [Fact]
  public async Task Resolve_AbsolutePath_IsReturnedUnchanged()
  {
    string workspace = Directory.CreateTempSubdirectory("ethang-exec-ws").FullName;
    try
    {
      CSharpScriptExecEngine engine = CreateEngine(workspace);

      // An absolute path is returned unchanged - the resolver never re-roots it.
      ExecRunResult run = await engine.ExecuteAsync(
          new ExecProgram("return ResolvePath(Path.Combine(Workspace, \"sub\", \"file.txt\"));"),
          ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

      Assert.Equal(ExecRunStatus.Completed, run.Status);
      Assert.Equal(
          Path.Combine(workspace, "sub", "file.txt"),
          run.Output.Trim(),
          StringComparer.OrdinalIgnoreCase);
    }
    finally
    {
      Directory.Delete(workspace, recursive: true);
    }
  }
}
