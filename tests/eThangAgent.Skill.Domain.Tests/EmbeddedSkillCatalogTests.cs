using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.Skill.Domain.Tests;

public class EmbeddedSkillCatalogTests
{
  private readonly EmbeddedSkillCatalog _catalog = new();

  private static readonly string[] ExpectedNames =
  [
      "brainstorming", "dispatching-parallel-agents", "ethang-tools-mapping",
        "executing-plans", "finishing-a-development-branch", "receiving-code-review",
        "requesting-code-review", "subagent-driven-development",
        "systematic-debugging", "test-driven-development", "using-git-worktrees",
        "using-skills", "verification-before-completion", "writing-plans",
        "writing-skills",
    ];

  [Fact]
  public async Task Lists_AllFifteenSkills_WithMetadata()
  {
    Result<IReadOnlyList<SkillDefinition>> r = await _catalog.ListAsync(TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess);
    string[] names = [.. r.Value.Select(s => s.Name).OrderBy(n => n)];
    Assert.Equal(ExpectedNames, names);
    Assert.All(r.Value, s =>
    {
      Assert.Equal(SkillSource.BuiltIn, s.Source);
      Assert.Equal(1, s.Version);
      Assert.False(string.IsNullOrWhiteSpace(s.Description));
    });
  }

  [Fact]
  public async Task Get_ReturnsVerbatimBody_MarkersIntact()
  {
    Result<SkillDefinition> r = await _catalog.GetAsync("brainstorming", ct: TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess);
    Assert.Contains("HARD-GATE", r.Value.Body, StringComparison.Ordinal);          // verbatim upstream marker
    Assert.StartsWith("# Brainstorming", r.Value.Body, StringComparison.Ordinal);   // body preserved verbatim after frontmatter split
  }

  [Fact]
  public async Task Get_UnknownName_Fails()
  {
    Result<SkillDefinition> r = await _catalog.GetAsync("not-a-skill", ct: TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal("SkillNotFound", r.Error.Code);
  }

  [Fact]
  public async Task MappingReference_IsListed_AndViewable()
  {
    Result<IReadOnlyList<SkillDefinition>> list = await _catalog.ListAsync(TestContext.Current.CancellationToken);
    Assert.Contains(list.Value!, s => s.Name == "ethang-tools-mapping");
    Result<SkillDefinition> get = await _catalog.GetAsync("ethang-tools-mapping", ct: TestContext.Current.CancellationToken);
    Assert.True(get.IsSuccess);
    Assert.Contains("skill_view", get.Value.Body, StringComparison.Ordinal);
    // Spec-required binding (SP3): skills that say "commit work" must bind to
    // the git_commit tool — never to raw shell commits.
    Assert.Contains("git_commit", get.Value.Body, StringComparison.Ordinal);
  }
}
