using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition.Tests;

/// <summary>Ten contract cases for SkillsBootstrapPromptProvider: the output
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

  [Fact]
  public void Build_DefaultConventional_InjectsConventionalStyleSkill()
  {
    // No style provider wired (e.g. hosts without a preference store) — the
    // documented Conventional default applies.
    string output = Build();

    Assert.Contains("from the fixed set", output, StringComparison.Ordinal);
    Assert.Contains("Conventional Commits", output, StringComparison.Ordinal);
  }

  [Fact]
  public void Build_SelectedGitmoji_InjectsGitmojiStyleSkill()
  {
    SkillsBootstrapPromptProvider provider = new(
        new EmbeddedSkillCatalog(), new FixedStyle(CommitStyle.Gitmoji));

    string output = provider.Build();

    Assert.Contains("from the gitmoji catalog", output, StringComparison.Ordinal);
  }

  [Fact]
  public void Build_SelectedStyle_NeverInjectsTheOtherStyleSkills()
  {
    SkillsBootstrapPromptProvider provider = new(
        new EmbeddedSkillCatalog(), new FixedStyle(CommitStyle.Gitmoji));

    string output = provider.Build();

    Assert.DoesNotContain("from the fixed set", output, StringComparison.Ordinal);
    Assert.DoesNotContain("the description stands alone", output, StringComparison.Ordinal);
  }

  [Fact]
  public void Build_MissingSelectedStyleSkill_ThrowsInvalidOperationException()
  {
    // A selected style whose built-in skill is absent is a packaging defect,
    // same rule as a missing using-skills.
    SkillsBootstrapPromptProvider provider = new(
        new CatalogWithoutBootstrapSkill(), new FixedStyle(CommitStyle.None));

    _ = Assert.Throws<InvalidOperationException>(provider.Build);
  }

  private sealed class FixedStyle(CommitStyle style) : ICommitStyleProvider
  {
    public Task<Result<CommitStyle>> GetAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success(style));
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
