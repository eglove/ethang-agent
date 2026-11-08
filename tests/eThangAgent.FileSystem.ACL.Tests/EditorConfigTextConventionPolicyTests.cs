using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

// Test helpers: sync temp-file IO and best-effort cleanup are deliberate.
#pragma warning disable CA1849 // Call async methods when in an async method
#pragma warning disable CA1031 // Do not catch general exception types

/// <summary>Convention discovery for writes: .editorconfig end_of_line (with section
///     matching, nearest file wins), then .gitattributes eol=, then the workspace's
///     existing majority, then the LF fallback. These drive the real resolver over real
///     temp trees — the discovery IS the behaviour, so faking the filesystem would test
///     nothing.</summary>
public sealed class EditorConfigTextConventionPolicyTests : IDisposable
{
  private readonly string _root = TestTempRoot.CreateDirectory("ethang-eol").FullName;

  public void Dispose()
  {
    try
    {
      Directory.Delete(_root, recursive: true);
    }
    catch (IOException)
    {
      // best-effort temp cleanup
    }
    catch (UnauthorizedAccessException)
    {
      // best-effort temp cleanup
    }

    GC.SuppressFinalize(this);
  }

  private void WriteFile(string relativePath, string content)
  {
    string path = Path.Combine(_root, relativePath);
    _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content);
  }

  private string In(string relativePath) => Path.Combine(_root, relativePath);

  // ---- .editorconfig ----

  [Fact]
  public void RootEditorConfig_EndOfLineLf_ResolvesLf()
  {
    WriteFile(".editorconfig", "root = true\n\n[*]\nend_of_line = lf\n");
    WriteFile("src/a.cs", "class A { }\n");

    EditorConfigTextConventionPolicy policy = new(_root);

    Assert.Equal(LineEnding.Lf, policy.Resolve(In("src/a.cs")).Ending);
  }

  [Fact]
  public void RootEditorConfig_EndOfLineCrLf_ResolvesCrLf()
  {
    WriteFile(".editorconfig", "root = true\n\n[*]\nend_of_line = crlf\n");

    EditorConfigTextConventionPolicy policy = new(_root);

    Assert.Equal(LineEnding.CrLf, policy.Resolve(In("new.txt")).Ending);
  }

  [Fact]
  public void SubdirectorySection_OverridesRootForThatSubtree()
  {
    // P2: the docs section says LF, the root says CRLF.
    WriteFile(".editorconfig", "root = true\n\n[*]\nend_of_line = crlf\n\n[docs/**]\nend_of_line = lf\n");

    EditorConfigTextConventionPolicy policy = new(_root);

    Assert.Equal(LineEnding.Lf, policy.Resolve(In("docs/guide.md")).Ending);
    Assert.Equal(LineEnding.CrLf, policy.Resolve(In("src/a.cs")).Ending);
  }

  [Fact]
  public void NestedEditorConfig_IsCloserAndWins()
  {
    WriteFile(".editorconfig", "root = true\n\n[*]\nend_of_line = crlf\n");
    WriteFile("vendor/.editorconfig", "[*]\nend_of_line = lf\n");

    EditorConfigTextConventionPolicy policy = new(_root);

    Assert.Equal(LineEnding.Lf, policy.Resolve(In("vendor/lib.c")).Ending);
    Assert.Equal(LineEnding.CrLf, policy.Resolve(In("src/lib.c")).Ending);
  }

  [Fact]
  public void SectionWithLaterMatch_WinsWithinOneFile()
  {
    WriteFile(".editorconfig", "root = true\n\n[*]\nend_of_line = lf\n\n[*.ts]\nend_of_line = crlf\n");

    EditorConfigTextConventionPolicy policy = new(_root);

    Assert.Equal(LineEnding.CrLf, policy.Resolve(In("app.ts")).Ending);
    Assert.Equal(LineEnding.Lf, policy.Resolve(In("app.cs")).Ending);
  }

  [Fact]
  public void EditorConfig_UnsetEndOfLine_DoesNotDecide()
  {
    // 'unset' explicitly withdraws a setting: it must not silently mean LF.
    WriteFile(".editorconfig", "root = true\n\n[*]\nend_of_line = crlf\n\n[*.md]\nend_of_line = unset\n");
    WriteFile(".gitattributes", "* text=auto eol=lf\n");

    EditorConfigTextConventionPolicy policy = new(_root);

    // Withdrawn for .md, so the next declaration down (gitattributes, LF) decides;
    // the equally-matching '[*]' section above it must NOT keep voting.
    Assert.Equal(LineEnding.Lf, policy.Resolve(In("notes.md")).Ending);
    Assert.Equal(LineEnding.CrLf, policy.Resolve(In("app.cs")).Ending);
  }

  // ---- .gitattributes ----

  [Fact]
  public void GitAttributes_EolLf_ResolvesLf()
  {
    WriteFile(".gitattributes", "* text=auto eol=lf\n");

    EditorConfigTextConventionPolicy policy = new(_root);

    Assert.Equal(LineEnding.Lf, policy.Resolve(In("a.txt")).Ending);
  }

  [Fact]
  public void GitAttributes_EolCrLf_ResolvesCrLf()
  {
    WriteFile(".gitattributes", "*.ps1 eol=crlf\n");

    EditorConfigTextConventionPolicy policy = new(_root);

    Assert.Equal(LineEnding.CrLf, policy.Resolve(In("build.ps1")).Ending);
  }

  [Fact]
  public void EditorConfig_BeatsGitAttributes()
  {
    WriteFile(".editorconfig", "root = true\n\n[*]\nend_of_line = lf\n");
    WriteFile(".gitattributes", "* text=auto eol=crlf\n");

    EditorConfigTextConventionPolicy policy = new(_root);

    Assert.Equal(LineEnding.Lf, policy.Resolve(In("a.txt")).Ending);
  }

  // ---- workspace majority ----

  [Fact]
  public void NoDeclaration_FallsBackToWorkspaceMajority()
  {
    // Three CRLF files and one LF file, nothing declaring a convention.
    WriteFile("a.txt", "one\r\ntwo\r\n");
    WriteFile("b.txt", "one\r\ntwo\r\n");
    WriteFile("c.txt", "one\r\ntwo\r\n");
    WriteFile("d.txt", "one\ntwo\n");

    EditorConfigTextConventionPolicy policy = new(_root);

    Assert.Equal(LineEnding.CrLf, policy.Resolve(In("new.txt")).Ending);
  }

  [Fact]
  public void EmptyWorkspace_FallsBackToLf()
  {
    EditorConfigTextConventionPolicy policy = new(_root);

    Assert.Equal(LineEnding.Lf, policy.Resolve(In("new.txt")).Ending);
  }

  [Fact]
  public void UnreadableDeclarationText_DoesNotThrow_AndFallsThrough()
  {
    // A corrupt setting is information, not a crash: the resolver ignores it and
    // keeps walking down the resolution order.
    WriteFile(".editorconfig", "root = true\n\n[*]\nend_of_line = sideways\n");
    WriteFile(".gitattributes", "* eol=crlf\n");

    EditorConfigTextConventionPolicy policy = new(_root);

    Assert.Equal(LineEnding.CrLf, policy.Resolve(In("a.txt")).Ending);
  }

  // ---- insert_final_newline ----

  [Fact]
  public void InsertFinalNewline_True_ResolvesTrue()
  {
    WriteFile(".editorconfig", "root = true\n\n[*]\ninsert_final_newline = true\n");

    EditorConfigTextConventionPolicy policy = new(_root);

    Assert.True(policy.Resolve(In("new.txt")).InsertFinalNewline);
  }

  [Fact]
  public void InsertFinalNewline_DeclaredFalse_ResolvesFalse()
  {
    WriteFile(".editorconfig", "root = true\n\n[*]\ninsert_final_newline = false\n");

    EditorConfigTextConventionPolicy policy = new(_root);

    Assert.False(policy.Resolve(In("new.txt")).InsertFinalNewline);
  }

  [Fact]
  public void InsertFinalNewline_Undeclared_LeavesTheTextVerbatim()
  {
    // Nothing declares the setting, so the tool writes what it was handed rather than
    // imposing a convention the project never asked for (issue #120's escape hatch).
    WriteFile(".editorconfig", "root = true\n\n[*]\nend_of_line = lf\n");

    EditorConfigTextConventionPolicy policy = new(_root);

    Assert.False(policy.Resolve(In("new.txt")).InsertFinalNewline);
  }

  [Fact]
  public void InsertFinalNewline_Unset_WithdrawsTheSetting()
  {
    WriteFile(".editorconfig",
        "root = true\n\n[*]\ninsert_final_newline = true\n\n[*.min.js]\ninsert_final_newline = unset\n");

    EditorConfigTextConventionPolicy policy = new(_root);

    Assert.True(policy.Resolve(In("app.cs")).InsertFinalNewline);
    Assert.False(policy.Resolve(In("bundle.min.js")).InsertFinalNewline);
  }

  [Fact]
  public void InsertFinalNewline_IsResolvedIndependentlyOfTheLineEnding()
  {
    WriteFile(".editorconfig", "root = true\n\n[*]\nend_of_line = crlf\ninsert_final_newline = true\n");

    EditorConfigTextConventionPolicy policy = new(_root);

    Assert.Equal(new TextConvention(LineEnding.CrLf, InsertFinalNewline: true), policy.Resolve(In("new.txt")));
  }

  [Fact]
  public void DirectoryLookup_WalksUpFromTheFilesOwnDirectory()
  {
    WriteFile(".editorconfig", "root = true\n\n[sub/**]\nend_of_line = crlf\n");
    _ = Directory.CreateDirectory(In("sub/deep"));

    EditorConfigTextConventionPolicy policy = new(_root);

    Assert.Equal(LineEnding.CrLf, policy.Resolve(In("sub/deep/new.txt")).Ending);
  }
}
