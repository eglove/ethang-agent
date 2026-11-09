using eThangAgent.Lsp.ACL;
using eThangAgent.TestSupport;

namespace eThangAgent.Lsp.ACL.Tests;

// A workspace is whatever directory the user opened, not one particular layout. The
// solution is searched for, because a project that keeps it in src/ is ordinary and
// looking only at the top level made that project invisible - no server started and every
// query was silent for a reason the user could not see.
public class LspWorkspaceLayoutTests
{
  [Fact]
  public void Solution_AtTheRoot_IsFound()
  {
    string root = TestTempRoot.CreateDirectory("lsp-layout-root").FullName;
    try
    {
      string solution = Path.Combine(root, "app.slnx");
      File.WriteAllText(solution, "<Solution />");
      Assert.Equal(solution, LspWorkspaceSolution.Find(root));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  /// <summary>A solution beside the project in src/ is the layout that used to be
  ///     invisible: the workspace was declared to hold no code, so no server started.</summary>
  [Fact]
  public void Solution_InsideAProjectDirectory_IsFound()
  {
    string root = TestTempRoot.CreateDirectory("lsp-layout-src").FullName;
    try
    {
      string src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
      string solution = Path.Combine(src, "app.slnx");
      File.WriteAllText(solution, "<Solution />");
      Assert.Equal(solution, LspWorkspaceSolution.Find(root));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  /// <summary>Build output carries its own project files. Loading one of those loads a
  ///     fragment of the workspace instead of the workspace, so it is never the answer.</summary>
  [Fact]
  public void Solution_InBuildOutput_IsIgnored()
  {
    string root = TestTempRoot.CreateDirectory("lsp-layout-obj").FullName;
    try
    {
      string obj = Directory.CreateDirectory(Path.Combine(root, "obj")).FullName;
      File.WriteAllText(Path.Combine(obj, "generated.slnx"), "<Solution />");
      Assert.Null(LspWorkspaceSolution.Find(root));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  /// <summary>The shallowest manifest wins, so a solution at the root is preferred over
  ///     one buried in a subdirectory.</summary>
  [Fact]
  public void Solution_ShallowestWins()
  {
    string root = TestTempRoot.CreateDirectory("lsp-layout-shallow").FullName;
    try
    {
      string shallow = Path.Combine(root, "app.slnx");
      File.WriteAllText(shallow, "<Solution />");
      string deep = Directory.CreateDirectory(Path.Combine(root, "src", "nested")).FullName;
      File.WriteAllText(Path.Combine(deep, "other.slnx"), "<Solution />");
      Assert.Equal(shallow, LspWorkspaceSolution.Find(root));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  /// <summary>A project file is not a solution: the server rejects it in solution/open and
  ///     never announces the load, so a workspace with only a .csproj must not be treated as
  ///     having one.</summary>
  [Fact]
  public void Solution_ProjectFileAlone_IsNotASolution()
  {
    string root = TestTempRoot.CreateDirectory("lsp-layout-csproj").FullName;
    try
    {
      File.WriteAllText(Path.Combine(root, "app.csproj"), "<Project />");
      Assert.Null(LspWorkspaceSolution.Find(root));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }
}
