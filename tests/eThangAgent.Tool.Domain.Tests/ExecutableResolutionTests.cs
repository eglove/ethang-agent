using eThangAgent.TestSupport;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>A command name is matched the way the Windows loader matches it: directory order
///     first, then PATHEXT order within a directory, first existing file wins. Every test below
///     builds the files it is about, so what is asserted is the RULE rather than this machine's
///     PATH.</summary>
public class ExecutableResolutionTests
{
  private const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD";

  // ---- The extension order ----

  [Fact]
  public void Extensions_AbsentPathExt_IsTheWindowsDefaultOrder() =>
      Assert.Equal((string[])[".COM", ".EXE", ".BAT", ".CMD"], ExecutableResolution.Extensions(null));

  [Fact]
  public void Extensions_EmptyPathExt_IsTheWindowsDefaultOrder() =>
      Assert.Equal((string[])[".COM", ".EXE", ".BAT", ".CMD"], ExecutableResolution.Extensions(string.Empty));

  [Fact]
  public void Extensions_DeclaredPathExt_KeepsItsOwnOrder() =>
      Assert.Equal((string[])[".EXE", ".CMD"], ExecutableResolution.Extensions(".EXE;.CMD"));

  // ---- Resolution ----

  [Fact]
  public void Resolve_CmdShim_IsTheAbsoluteCmdPath()
  {
    string directory = NewDirectory();
    string shim = WriteFile(directory, "npm.cmd");

    string? resolved = ExecutableResolution.Resolve("npm", [directory], DefaultPathExt);

    Assert.Equal(shim, resolved, ignoreCase: true);
    Assert.True(Path.IsPathFullyQualified(resolved!), "the resolved path is absolute");
    Assert.True(File.Exists(resolved!), "the resolved path names the file that exists");
  }

  [Fact]
  public void Resolve_ExtensionlessFileAndCmdPresent_IsTheCmd()
  {
    string directory = NewDirectory();
    _ = WriteFile(directory, "npm");
    string shim = WriteFile(directory, "npm.cmd");

    Assert.Equal(shim, ExecutableResolution.Resolve("npm", [directory], DefaultPathExt), ignoreCase: true);
  }

  [Fact]
  public void Resolve_Exe_IsTheAbsoluteExePath()
  {
    string directory = NewDirectory();
    string executable = WriteFile(directory, "node.exe");

    Assert.Equal(executable, ExecutableResolution.Resolve("node", [directory], DefaultPathExt), ignoreCase: true);
  }

  [Fact]
  public void Resolve_UnknownName_IsNull()
  {
    string directory = NewDirectory();
    _ = WriteFile(directory, "npm.cmd");

    Assert.Null(ExecutableResolution.Resolve("nosuchtool", [directory], DefaultPathExt));
  }

  [Fact]
  public void Resolve_NoDirectories_IsNull() =>
      Assert.Null(ExecutableResolution.Resolve("npm", [], DefaultPathExt));

  [Fact]
  public void Resolve_EarliestDirectoryWins()
  {
    string first = NewDirectory();
    string second = NewDirectory();
    string shim = WriteFile(first, "npm.cmd");
    _ = WriteFile(second, "npm.exe");

    Assert.Equal(shim, ExecutableResolution.Resolve("npm", [first, second], DefaultPathExt), ignoreCase: true);
  }

  [Fact]
  public void Resolve_PathExtOrderDecidesWithinOneDirectory()
  {
    string directory = NewDirectory();
    _ = WriteFile(directory, "tool.exe");
    string script = WriteFile(directory, "tool.cmd");

    Assert.Equal(script, ExecutableResolution.Resolve("tool", [directory], ".CMD;.EXE"), ignoreCase: true);
  }

  [Fact]
  public void Resolve_DeclaredPathExtWithoutCmd_DoesNotFindTheShim()
  {
    string directory = NewDirectory();
    _ = WriteFile(directory, "npm.cmd");

    Assert.Null(ExecutableResolution.Resolve("npm", [directory], ".COM;.EXE;.BAT"));
  }

  [Fact]
  public void Resolve_BlankDirectoryEntry_IsSkipped()
  {
    string directory = NewDirectory();
    string shim = WriteFile(directory, "npm.cmd");

    Assert.Equal(shim, ExecutableResolution.Resolve("npm", ["", "   ", directory], DefaultPathExt), ignoreCase: true);
  }

  [Fact]
  public void Resolve_IsCaseInsensitive()
  {
    string directory = NewDirectory();
    string shim = WriteFile(directory, "npm.cmd");

    Assert.Equal(shim, ExecutableResolution.Resolve("NPM", [directory], DefaultPathExt), ignoreCase: true);
  }

  // ---- A qualified name says where to look ----

  [Fact]
  public void Resolve_QualifiedName_IsResolvedAgainstItsOwnDirectory()
  {
    string directory = NewDirectory();
    string shim = WriteFile(directory, "x.cmd");

    string? resolved = ExecutableResolution.Resolve(
        Path.Combine(directory, "x"), [NewDirectory()], DefaultPathExt);

    Assert.Equal(shim, resolved, ignoreCase: true);
  }

  [Fact]
  public void Resolve_QualifiedName_DoesNotSearchTheSuppliedDirectories()
  {
    string searched = NewDirectory();
    _ = WriteFile(searched, "x.cmd");
    string elsewhere = NewDirectory();

    Assert.Null(ExecutableResolution.Resolve(
        Path.Combine(elsewhere, "x"), [searched], DefaultPathExt));
  }

  [Fact]
  public void IsQualified_NamesWithASeparatorAreQualified()
  {
    Assert.True(ExecutableResolution.IsQualified(Path.Combine("tools", "x.cmd")));
    Assert.True(ExecutableResolution.IsQualified(Path.Combine(TestTempRoot.Root, "x.cmd")));
    Assert.False(ExecutableResolution.IsQualified("x.cmd"));
  }

  // ---- Fixtures ----

  private static string NewDirectory() => TestTempRoot.CreateDirectory("executable-resolution").FullName;

  /// <summary>Creates an empty file and returns its absolute path.</summary>
  private static string WriteFile(string directory, string fileName)
  {
    string path = Path.Combine(directory, fileName);
    File.WriteAllText(path, string.Empty);
    return path;
  }
}
