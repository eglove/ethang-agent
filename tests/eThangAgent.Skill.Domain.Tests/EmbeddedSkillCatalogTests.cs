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
        "using-superpowers", "verification-before-completion", "writing-plans",
        "writing-skills",
    ];

    [Fact]
    public async Task Lists_AllFifteenSkills_WithMetadata()
    {
        var r = await _catalog.ListAsync();
        Assert.True(r.IsSuccess);
        var names = r.Value!.Select(s => s.Name).OrderBy(n => n).ToArray();
        Assert.Equal(ExpectedNames, names);
        Assert.All(r.Value!, s =>
        {
            Assert.Equal(SkillSource.BuiltIn, s.Source);
            Assert.Equal(1, s.Version);
            Assert.False(string.IsNullOrWhiteSpace(s.Description));
        });
    }

    [Fact]
    public async Task Get_ReturnsVerbatimBody_MarkersIntact()
    {
        var r = await _catalog.GetAsync("brainstorming");
        Assert.True(r.IsSuccess);
        Assert.Contains("HARD-GATE", r.Value!.Body);          // verbatim upstream marker
        Assert.StartsWith("# Brainstorming", r.Value.Body);   // body preserved verbatim after frontmatter split
    }

    [Fact]
    public async Task Get_UnknownName_Fails()
    {
        var r = await _catalog.GetAsync("not-a-skill");
        Assert.False(r.IsSuccess);
        Assert.Equal("SkillNotFound", r.Error!.Code);
    }

    [Fact]
    public async Task MappingReference_IsListed_AndViewable()
    {
        var list = await _catalog.ListAsync();
        Assert.Contains(list.Value!, s => s.Name == "ethang-tools-mapping");
        var get = await _catalog.GetAsync("ethang-tools-mapping");
        Assert.True(get.IsSuccess);
        Assert.Contains("skill_view", get.Value!.Body);
    }
}
