// Best-effort temp-dir cleanup in Dispose is deliberate: swallowing any fault
// keeps a failing test from cascading into teardown noise (CA1031).
#pragma warning disable CA1031 // Do not catch general exception types

namespace eThangAgent.Composition.Tests;

/// <summary>Exercises the workspace-instructions injection against REAL files in a temp
/// directory: presence/absence of AGENTS.md at the chosen root decides what the provider
/// contributes to the composite system prompt.</summary>
public sealed class WorkspaceInstructionsPromptProviderTests : IDisposable
{
  private readonly string _dir;

  public WorkspaceInstructionsPromptProviderTests()
  {
    _dir = Path.Combine(Path.GetTempPath(), "ethang-wipp-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(_dir);
  }

  public void Dispose()
  {
    GC.SuppressFinalize(this);
    try
    {
      Directory.Delete(_dir, recursive: true);
    }
    catch { /* best effort */ }
  }

  [Fact]
  public void Without_Agents_File_Build_Is_Empty()
  {
    WorkspaceInstructionsPromptProvider provider = new(_dir);
    Assert.True(string.IsNullOrWhiteSpace(provider.Build()));
  }

  [Fact]
  public void With_Agents_File_Contents_Are_Embedded_And_Announced_As_Read()
  {
    const string markdown = "# Project Rules\n\n- All scripts are PowerShell.\n";
    File.WriteAllText(Path.Combine(_dir, "AGENTS.md"), markdown);

    string text = new WorkspaceInstructionsPromptProvider(_dir).Build();

    Assert.Contains("has been read", text, StringComparison.Ordinal);
    Assert.Contains(markdown.TrimEnd(), text, StringComparison.Ordinal); // verbatim body survives
    Assert.Contains("<agents-file source=", text, StringComparison.Ordinal);
    Assert.EndsWith("</agents-file>", text.TrimEnd(), StringComparison.Ordinal);
  }

  [Fact]
  public void Working_Directory_Line_Carries_The_Full_Root_Path()
  {
    File.WriteAllText(Path.Combine(_dir, "AGENTS.md"), "x");

    string text = new WorkspaceInstructionsPromptProvider(_dir).Build();

    Assert.Contains("Working directory: " + Path.GetFullPath(_dir), text, StringComparison.Ordinal);
  }

  [Fact]
  public void Relative_Root_Is_Normalized_To_Full_Path()
  {
    string cwd = Path.GetFullPath(".");
    File.WriteAllText(Path.Combine(cwd, "AGENTS.md"), "x");
    try
    {
      string text = new WorkspaceInstructionsPromptProvider(".").Build();

      Assert.Contains("Working directory: " + cwd, text, StringComparison.Ordinal);
      Assert.DoesNotContain("Working directory: .\n", text, StringComparison.Ordinal);
    }
    finally
    {
      File.Delete(Path.Combine(cwd, "AGENTS.md"));
    }
  }
}
