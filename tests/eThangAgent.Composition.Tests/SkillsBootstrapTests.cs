using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.Composition.Tests;

/// <summary>Six contract cases for SkillsBootstrapPromptProvider: the output
/// wraps the verbatim using-skills skill (frontmatter included) plus the inline
/// tool-mapping constant in EXTREMELY_IMPORTANT tags, each tag occurring exactly
/// once; a catalog missing the built-in skill is a packaging defect that throws.</summary>
public class SkillsBootstrapTests
{
  // Distinctive sentence lifted from the body of the embedded
  // src/eThangAgent.Skill.Domain/skills/using-skills/SKILL.md.
  private const string StableBodyPhrase =
      "Invoke relevant or requested skills BEFORE any response or action";

  private static string Build() =>
      new SkillsBootstrapPromptProvider(new EmbeddedSkillCatalog()).Build();

  [Fact]
  public void Build_WrapsOutputInExtremelyImportantTags()
  {
    string output = Build();

    Assert.StartsWith("<EXTREMELY_IMPORTANT>", output, StringComparison.Ordinal);
    Assert.EndsWith("</EXTREMELY_IMPORTANT>", output, StringComparison.Ordinal);
  }

  [Fact]
  public void Build_ContainsVerbatimUsingSkillsSkill()
  {
    string output = Build();

    Assert.Contains("name: using-skills", output, StringComparison.Ordinal);
    Assert.Contains(StableBodyPhrase, output, StringComparison.Ordinal);
  }

  [Fact]
  public void Build_ContainsEveryMappingKey()
  {
    string output = Build();

    foreach (string? key in new[]
             {
                     "read", "write", "edit", "search_files", "exec",
                     "spawn", "todo", "skill_view", "skill_list", "clarify",
                 })
    {
      Assert.Contains(key, output, StringComparison.Ordinal);
    }
  }

  [Fact]
  public void Build_MarksSkillAsAlreadyActive() => Assert.Contains("ALREADY ACTIVE", Build(), StringComparison.Ordinal);

  [Fact]
  public void Build_WrapperMarkersOccurExactlyOnceEach()
  {
    string output = Build();

    Assert.Equal(1, CountOccurrences(output, "<EXTREMELY_IMPORTANT>"));
    Assert.Equal(1, CountOccurrences(output, "</EXTREMELY_IMPORTANT>"));
  }

  [Fact]
  public void Build_CatalogMissingUsingSkills_ThrowsInvalidOperationException()
  {
    SkillsBootstrapPromptProvider provider = new(new CatalogWithoutBootstrapSkill());

    _ = Assert.Throws<InvalidOperationException>(provider.Build);
  }

  private static int CountOccurrences(string text, string marker)
  {
    int count = 0;
    int index = 0;
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
        => Task.FromResult(Result.Success<IReadOnlyList<SkillDefinition>>([]));

    public Task<Result<SkillDefinition>> GetAsync(string name, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<SkillDefinition>(
            new DomainError("SkillNotFound",
                $"No built-in skill named '{name}'. Use skill_list to see available skills.")));
  }
}
