using eThangAgent.Composition;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.CLI.Tests;

/// <summary>Six contract cases for SuperpowersBootstrapPromptProvider: the output
/// wraps the verbatim using-superpowers skill (frontmatter included) plus the inline
/// tool-mapping constant in EXTREMELY_IMPORTANT tags, each tag occurring exactly
/// once; a catalog missing the built-in skill is a packaging defect that throws.</summary>
public class SuperpowersBootstrapTests
{
    // Distinctive sentence lifted from the body of the embedded
    // src/eThangAgent.Skill.Domain/skills/using-superpowers/SKILL.md.
    private const string StableBodyPhrase =
        "Invoke relevant or requested skills BEFORE any response or action";

    private static string Build() =>
        new SuperpowersBootstrapPromptProvider(new EmbeddedSkillCatalog()).Build();

    [Fact]
    public void Build_WrapsOutputInExtremelyImportantTags()
    {
        var output = Build();

        Assert.StartsWith("<EXTREMELY_IMPORTANT>", output);
        Assert.EndsWith("</EXTREMELY_IMPORTANT>", output);
    }

    [Fact]
    public void Build_ContainsVerbatimUsingSuperpowersSkill()
    {
        var output = Build();

        Assert.Contains("name: using-superpowers", output);
        Assert.Contains(StableBodyPhrase, output);
    }

    [Fact]
    public void Build_ContainsEveryMappingKey()
    {
        var output = Build();

        foreach (var key in new[]
                 {
                     "read", "write", "edit", "search_files", "exec",
                     "spawn", "todo", "skill_view", "skill_list", "clarify",
                 })
            Assert.Contains(key, output);
    }

    [Fact]
    public void Build_MarksSkillAsAlreadyActive()
    {
        Assert.Contains("ALREADY ACTIVE", Build());
    }

    [Fact]
    public void Build_WrapperMarkersOccurExactlyOnceEach()
    {
        var output = Build();

        Assert.Equal(1, CountOccurrences(output, "<EXTREMELY_IMPORTANT>"));
        Assert.Equal(1, CountOccurrences(output, "</EXTREMELY_IMPORTANT>"));
    }

    [Fact]
    public void Build_CatalogMissingUsingSuperpowers_ThrowsInvalidOperationException()
    {
        var provider = new SuperpowersBootstrapPromptProvider(new CatalogWithoutBootstrapSkill());

        Assert.Throws<InvalidOperationException>(provider.Build);
    }

    private static int CountOccurrences(string text, string marker)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += marker.Length;
        }

        return count;
    }

    private sealed class CatalogWithoutBootstrapSkill : ISkillCatalog
    {
        public Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default)
            => Task.FromResult(Result<IReadOnlyList<SkillDefinition>>.Success([]));

        public Task<Result<SkillDefinition>> GetAsync(string name, CancellationToken ct = default)
            => Task.FromResult(Result<SkillDefinition>.Failure(
                new Error("SkillNotFound",
                    $"No built-in skill named '{name}'. Use skill_list to see available skills.")));
    }
}
