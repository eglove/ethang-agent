using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition.Tests;

/// <summary>Eleven contract cases for SkillsBootstrapPromptProvider: the output
/// renders the verbatim using-skills skill (frontmatter header + body) plus the
/// verbatim ethang-tools-mapping skill body — BOTH read from the skill catalog —
/// the selected commit-style guidance, the ASD-STE100 user-message style rule,
/// and the already-active notice as a PLAIN session contract: no emphasis tags
/// anywhere. The always-on skill listing is a separate provider; this one
/// injects the contract skill, tools mapping, commit style, and STE rule. A
/// catalog missing a built-in skill is a packaging defect that throws.</summary>
public class SkillsBootstrapTests
{
  // Distinctive sentence lifted from the body of the embedded
  // src/eThangAgent.Skill.Domain/skills/using-skills/SKILL.md (the general session contract).
  private const string StableBodyPhrase =
      "current task is better than improvising the same procedure from scratch";

  private static string Build() =>
      new SkillsBootstrapPromptProvider(new EmbeddedSkillCatalog()).Build();

  [Fact]
  public void Build_ContainsNoExtremelyImportantEmphasisTags()
  {
    string output = Build();

    Assert.DoesNotContain("EXTREMELY_IMPORTANT", output, StringComparison.Ordinal);
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
                     "read", "write", "edit", "exec",
                     "spawn", "todo", "skill_view", "skill_list",
                 })
    {
      Assert.Contains(key, output, StringComparison.Ordinal);
    }
  }

  [Fact]
  public void Build_MarksSkillAsAlreadyActive() => Assert.Contains("ALREADY ACTIVE", Build(), StringComparison.Ordinal);

  [Fact]
  public void Build_LeadsWithSkillsFrontmatter_AndEndsWithAlreadyActiveNotice()
  {
    string output = Build();

    Assert.StartsWith("---\nname: using-skills", output, StringComparison.Ordinal);
    Assert.EndsWith("This bootstrap is injected once per session.", output, StringComparison.Ordinal);
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
  public void Build_InjectsUserMessageStyleRule_AsdSte100()
  {
    string output = Build();

    Assert.Contains("ASD-STE100", output, StringComparison.Ordinal);
    Assert.Contains("Simplified Technical English", output, StringComparison.Ordinal);
    Assert.Contains("messages to the user", output, StringComparison.Ordinal);
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
