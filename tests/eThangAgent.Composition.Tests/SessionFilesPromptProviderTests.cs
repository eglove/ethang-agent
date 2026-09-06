using System.Text.Json;

namespace eThangAgent.Composition.Tests;

/// <summary>E2: the settings-driven session-files prompt provider. The provider
///     parses the stored preference lists itself, so a corrupt list is an annotation
///     at the same place the decision is made. Union order (global first), checkbox
///     filtering, missing-file annotations, 512 KB truncation, and truly-empty are
///     pinned here.</summary>
public sealed class SessionFilesPromptProviderTests : IDisposable
{
  private readonly string _dir = Directory.CreateTempSubdirectory("ethang-sfpp").FullName;

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
    File.WriteAllText(Path.Combine(_dir, "a.md"), "AAA");
    SessionFilesPromptProvider provider = new(_dir,
        List(new SessionFileEntry(Path.Combine(_dir, "a.md"), true)), null);
    string text = provider.Build();
    Assert.Contains("Working directory: " + Path.GetFullPath(_dir), text, StringComparison.Ordinal);
    Assert.Contains("<session-file source=", text, StringComparison.Ordinal);
    Assert.Contains("AAA", text, StringComparison.Ordinal);
    Assert.EndsWith("</session-file>", text.TrimEnd(), StringComparison.Ordinal);
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
}
