using eThangAgent.Composition;

namespace eThangAgent.Composition.Tests;

/// <summary>Exercises the workspace-instructions injection against REAL files in a temp
/// directory: presence/absence of AGENTS.md at the chosen root decides what the provider
/// contributes to the composite system prompt.</summary>
public class WorkspaceInstructionsPromptProviderTests : IDisposable
{
    private readonly string _dir;

    public WorkspaceInstructionsPromptProviderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ethang-wipp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Without_Agents_File_Build_Is_Empty()
    {
        var provider = new WorkspaceInstructionsPromptProvider(_dir);
        Assert.True(string.IsNullOrWhiteSpace(provider.Build()));
    }

    [Fact]
    public void With_Agents_File_Contents_Are_Embedded_And_Announced_As_Read()
    {
        const string markdown = "# Project Rules\n\n- All scripts are PowerShell.\n";
        File.WriteAllText(Path.Combine(_dir, "AGENTS.md"), markdown);

        var text = new WorkspaceInstructionsPromptProvider(_dir).Build();

        Assert.Contains("has been read", text);
        Assert.Contains(markdown.TrimEnd(), text); // verbatim body survives
        Assert.Contains("<agents-file source=", text);
        Assert.EndsWith("</agents-file>", text.TrimEnd());
    }

    [Fact]
    public void Working_Directory_Line_Carries_The_Full_Root_Path()
    {
        File.WriteAllText(Path.Combine(_dir, "AGENTS.md"), "x");

        var text = new WorkspaceInstructionsPromptProvider(_dir).Build();

        Assert.Contains("Working directory: " + Path.GetFullPath(_dir), text);
    }

    [Fact]
    public void Relative_Root_Is_Normalized_To_Full_Path()
    {
        var cwd = Path.GetFullPath(".");
        File.WriteAllText(Path.Combine(cwd, "AGENTS.md"), "x");
        try
        {
            var text = new WorkspaceInstructionsPromptProvider(".").Build();

            Assert.Contains("Working directory: " + cwd, text);
            Assert.DoesNotContain("Working directory: .\n", text);
        }
        finally { File.Delete(Path.Combine(cwd, "AGENTS.md")); }
    }
}