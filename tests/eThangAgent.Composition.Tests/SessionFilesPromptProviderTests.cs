using System.Text.Json;
using eThangAgent.TestSupport;

namespace eThangAgent.Composition.Tests;

/// <summary>E2: the settings-driven session-files prompt provider. The provider
///     parses the stored preference lists itself, so a corrupt list is an annotation
///     at the same place the decision is made. Union order (global first), checkbox
///     filtering, missing-file annotations, 512 KB truncation, and truly-empty are
///     pinned here.</summary>
public sealed class SessionFilesPromptProviderTests : IDisposable
{
  private readonly string _dir = TestTempRoot.CreateDirectory("ethang-sfpp").FullName;

  public void Dispose()
  {
    GC.SuppressFinalize(this);
    try
    {
      Directory.Delete(_dir, recursive: true);
    }
    catch (IOException)
    {
      // best effort
    }
  }

  private static string List(params SessionFileEntry[] entries) =>
      JsonSerializer.Serialize(entries);

  [Fact]
  public void Nothing_Configured_Renders_Empty()
  {
    SessionFilesPromptProvider provider = new(_dir, null, null);
    Assert.True(string.IsNullOrWhiteSpace(provider.Build()));
  }

  [Fact]
  public void Disabled_Entries_Load_Nothing_And_Alone_Render_Empty()
  {
    File.WriteAllText(Path.Combine(_dir, "SKIMMED.md"), "content");
    SessionFilesPromptProvider provider = new(_dir,
        List(new SessionFileEntry(Path.Combine(_dir, "SKIMMED.md"), false)), null);
    Assert.True(string.IsNullOrWhiteSpace(provider.Build()));
  }

  [Fact]
  public void Global_Files_Render_Before_Workspace_Files()
  {
    File.WriteAllText(Path.Combine(_dir, "g.md"), "GLOBAL-CONTENT");
    File.WriteAllText(Path.Combine(_dir, "w.md"), "WORKSPACE-CONTENT");
    SessionFilesPromptProvider provider = new(_dir,
        List(new SessionFileEntry(Path.Combine(_dir, "g.md"), true)),
        List(new SessionFileEntry(Path.Combine(_dir, "w.md"), true)));
    string text = provider.Build();
    Assert.True(text.IndexOf("GLOBAL-CONTENT", StringComparison.Ordinal) < text.IndexOf("WORKSPACE-CONTENT", StringComparison.Ordinal));
  }

  [Fact]
  public void Each_File_Renders_In_A_Session_File_Block()
  {
    string path = Path.Combine(_dir, "a.md");
    File.WriteAllText(path, "AAA");
    SessionFilesPromptProvider provider = new(_dir,
        List(new SessionFileEntry(path, true)), null);
    string text = provider.Build();
    Assert.Contains("Working directory: " + Path.GetFullPath(_dir), text, StringComparison.Ordinal);
    Assert.Contains(
        "<session-file scope=\"global\" path=\"" + Path.GetFullPath(path) + "\">",
        text, StringComparison.Ordinal);
    Assert.Contains("AAA", text, StringComparison.Ordinal);
    Assert.EndsWith("</session-file>", text.TrimEnd(), StringComparison.Ordinal);
  }

  // The user's three-section reading problem: the global file and the workspace file
  // used to render as one undifferentiated run of text behind a single lead-in
  // sentence, so a file's own H1 ("# AGENTS.md") read as the title of everything.
  // Every block now names its own scope and its own path.
  [Fact]
  public void Each_Block_Names_Its_Own_Scope_And_Path()
  {
    string globalPath = Path.Combine(_dir, "g.md");
    string workspacePath = Path.Combine(_dir, "w.md");
    File.WriteAllText(globalPath, "GLOBAL-CONTENT");
    File.WriteAllText(workspacePath, "WORKSPACE-CONTENT");
    SessionFilesPromptProvider provider = new(_dir,
        List(new SessionFileEntry(globalPath, true)),
        List(new SessionFileEntry(workspacePath, true)));
    string text = provider.Build();

    Assert.Contains(
        "<session-file scope=\"global\" path=\"" + Path.GetFullPath(globalPath) + "\">",
        text, StringComparison.Ordinal);
    Assert.Contains(
        "<session-file scope=\"workspace\" path=\"" + Path.GetFullPath(workspacePath) + "\">",
        text, StringComparison.Ordinal);
    Assert.Equal(2, Occurrences(text, "</session-file>"));
  }

  [Fact]
  public void Preamble_Names_The_Scopes_Instead_Of_One_Undifferentiated_Body()
  {
    File.WriteAllText(Path.Combine(_dir, "a.md"), "AAA");
    SessionFilesPromptProvider provider = new(_dir,
        List(new SessionFileEntry(Path.Combine(_dir, "a.md"), true)), null);
    string text = provider.Build();

    Assert.Contains("global file applies to every workspace", text, StringComparison.Ordinal);
    Assert.DoesNotContain("verbatim contents follow", text, StringComparison.Ordinal);
  }

  private static int Occurrences(string text, string needle)
  {
    int count = 0;
    int at = text.IndexOf(needle, StringComparison.Ordinal);
    while (at >= 0)
    {
      count++;
      at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
    }

    return count;
  }

  [Fact]
  public void Missing_File_Is_Annotated_And_Skipped_Not_A_Failure()
  {
    SessionFilesPromptProvider provider = new(_dir,
        List(new SessionFileEntry(Path.Combine(_dir, "ghost.md"), true)), null);
    string text = provider.Build();
    Assert.Contains("[session-files]", text, StringComparison.Ordinal);
    Assert.Contains("ghost.md", text, StringComparison.Ordinal);
    Assert.DoesNotContain("<session-file", text, StringComparison.Ordinal);
  }

  [Fact]
  public void Oversized_File_Is_Truncated_With_A_Visible_Marker()
  {
    string big = new('x', 600 * 1024);
    File.WriteAllText(Path.Combine(_dir, "big.md"), big);
    SessionFilesPromptProvider provider = new(_dir,
        List(new SessionFileEntry(Path.Combine(_dir, "big.md"), true)), null);
    string text = provider.Build();
    Assert.Contains("[session-files] truncated", text, StringComparison.Ordinal);
    Assert.DoesNotContain(big, text, StringComparison.Ordinal);
  }

  [Fact]
  public void Corrupt_Global_List_Is_Annotated_Not_Silent()
  {
    string text = new SessionFilesPromptProvider(_dir, "not json at all", null).Build();
    Assert.Contains("[session-files]", text, StringComparison.Ordinal);
    Assert.Contains("global", text, StringComparison.Ordinal);
  }

  [Fact]
  public void Corrupt_Workspace_List_Is_Annotated_Not_Silent()
  {
    string text = new SessionFilesPromptProvider(_dir, null, "{").Build();
    Assert.Contains("[session-files]", text, StringComparison.Ordinal);
    Assert.Contains("workspace", text, StringComparison.Ordinal);
  }

  // Issue #267: Build() is a PURE RENDER. It used to re-read every configured file on
  // every call while its doc claimed purity, and Build runs at least three times per
  // iteration - so editing a session file mid-turn rewrote the first bytes of the
  // request and silently invalidated the entire prompt cache. The snapshot is taken at
  // construction; these tests pin that a later file change cannot reach the wire.
  [Fact]
  public void Build_IsPure_RepeatedCallsAreByteIdentical()
  {
    File.WriteAllText(Path.Combine(_dir, "a.md"), "AAA");
    SessionFilesPromptProvider provider = new(_dir,
        List(new SessionFileEntry(Path.Combine(_dir, "a.md"), true)), null);

    Assert.Equal(provider.Build(), provider.Build());
  }

  [Fact]
  public void Build_DoesNotReReadFiles_AfterConstruction()
  {
    string path = Path.Combine(_dir, "a.md");
    File.WriteAllText(path, "ORIGINAL");
    SessionFilesPromptProvider provider = new(_dir,
        List(new SessionFileEntry(path, true)), null);

    File.WriteAllText(path, "REWRITTEN-AFTER-CONSTRUCTION");

    string text = provider.Build();
    Assert.Contains("ORIGINAL", text, StringComparison.Ordinal);
    Assert.DoesNotContain("REWRITTEN-AFTER-CONSTRUCTION", text, StringComparison.Ordinal);
  }

  [Fact]
  public void Build_DoesNotReReadFiles_BetweenCalls()
  {
    string path = Path.Combine(_dir, "a.md");
    File.WriteAllText(path, "FIRST");
    SessionFilesPromptProvider provider = new(_dir,
        List(new SessionFileEntry(path, true)), null);
    string first = provider.Build();

    File.WriteAllText(path, "SECOND");

    Assert.Equal(first, provider.Build());
  }
}
