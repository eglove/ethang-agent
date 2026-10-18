using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.Skill.Domain.Tests;

public class EmbeddedSkillCatalogTests
{
  private readonly EmbeddedSkillCatalog _catalog = new();

  private static readonly string[] ExpectedNames =
  [
      "commit-style-conventional", "commit-style-gitmoji",
        "commit-style-none", "dispatching-parallel-agents", "ethang-tools-mapping",
        "executing-plans", "experiment-driven-development", "finishing-a-development-branch", "grill",
        "receiving-code-review", "requesting-code-review", "scientific-method", "systematic-debugging",
        "test-driven-development", "using-git-worktrees", "using-skills",
        "verification-before-completion", "writing-plans", "writing-skills",
    ];

  [Fact]
  public async Task Lists_AllNineteenSkills_WithMetadata()
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
  public async Task Get_ReturnsVerbatimBody_AfterFrontmatterSplit()
  {
    Result<SkillDefinition> r = await _catalog.GetAsync("grill", ct: TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess);
    string body = r.Value.Body;
    // Body preserved verbatim after frontmatter split: the frontmatter is gone and the
    // skill's own prose survives intact. The opening is pinned positionally, so a leaked
    // fence, a leaked key, or a shifted first line all fail here rather than passing
    // because the prose merely appears somewhere.
    Assert.StartsWith("This is the observation leg of the scientific loop", body, StringComparison.Ordinal);
    Assert.False(body.StartsWith("---", StringComparison.Ordinal), "frontmatter fence leaked into the body");
    Assert.DoesNotContain("description:", body, StringComparison.Ordinal);
    Assert.DoesNotContain("name: grill", body, StringComparison.Ordinal);
    Assert.Contains("Interview the user relentlessly", body, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Sdd_Requires_WatchedRed_Evidence_And_Tdd_Skill_Captures_It()
  {
    // Grand plan: watched-RED is unverifiable downstream — every SDD implementer
    // report must carry a RED-transcript artifact (command + failing output
    // verbatim), the task reviewer audits it, and the TDD skill's checklist
    // tells the implementer to capture it.
    Result<SkillDefinition> sdd = await _catalog.GetAsync("experiment-driven-development", ct: TestContext.Current.CancellationToken);
    Assert.True(sdd.IsSuccess);
    Assert.Contains("RED transcript", sdd.Value.Body, StringComparison.Ordinal);
    Assert.Contains("exact test command", sdd.Value.Body, StringComparison.Ordinal);
    Assert.Contains("Audit the RED transcript", sdd.Value.Body, StringComparison.Ordinal);

    Result<SkillDefinition> tdd = await _catalog.GetAsync("test-driven-development", ct: TestContext.Current.CancellationToken);
    Assert.True(tdd.IsSuccess);
    Assert.Contains("RED transcript", tdd.Value.Body, StringComparison.Ordinal);
  }

  private static readonly (string Skill, string LegHeader, string Anchor)[] PipelineLegHeaders =
  [
      ("grill", "This is the observation leg of the scientific loop", "Interview the user relentlessly"),
      ("writing-plans", "This is the prediction leg of the scientific loop", "Write comprehensive implementation plans"),
      ("experiment-driven-development", "This carries the experiment and external-verdict legs", "Execute a plan by dispatching"),
      ("requesting-code-review", "This is the external-verdict leg", "Dispatch a code reviewer subagent"),
      ("receiving-code-review", "This is the analysis leg", "Code review requires technical evaluation"),
  ];

  [Fact]
  public async Task PipelineSkills_NameTheLegBeforeTheOpeningProse()
  {
    // Grand plan: the pipeline skills name which loop leg they carry, so the
    // router's table and the carrying skill agree on the same vocabulary.
    foreach ((string skill, string legHeader, string anchor) in PipelineLegHeaders)
    {
      Result<SkillDefinition> r = await _catalog.GetAsync(skill, ct: TestContext.Current.CancellationToken);
      Assert.True(r.IsSuccess, skill);
      string body = r.Value.Body;
      int legIndex = body.IndexOf(legHeader, StringComparison.Ordinal);
      Assert.True(legIndex >= 0, $"{skill}: missing leg header '{legHeader}'");
      int anchorIndex = body.IndexOf(anchor, StringComparison.Ordinal);
      Assert.True(anchorIndex >= 0, $"{skill}: missing anchor '{anchor}'");
      Assert.True(anchorIndex > legIndex, $"{skill}: leg header must precede '{anchor}'");
    }
  }

  [Fact]
  public async Task FinishSkill_Carries_LedgerGate_And_UntestedPredictionRuling()
  {
    // The branch-finish menu is the advisory layer's enforcement point: a branch
    // carrying an open prediction is a branch carrying an untested belief, so the
    // finish skill checks the ledger before it presents the integration menu.
    Result<SkillDefinition> r = await _catalog.GetAsync("finishing-a-development-branch", ct: TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess);
    string body = r.Value.Body;
    Assert.Contains("## Step 1: Verify Tests and Ledger", body, StringComparison.Ordinal);
    Assert.Contains("### The ledger gate", body, StringComparison.Ordinal);
    Assert.Contains("open prediction", body, StringComparison.Ordinal);
    Assert.Contains("record-verdict", body, StringComparison.Ordinal);
    Assert.Contains("An untested prediction is a belief, not a result", body, StringComparison.Ordinal);
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
