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
        "receiving-code-review", "requesting-code-review", "reviewing-plans", "scientific-method",
        "systematic-debugging",
        "test-driven-development", "using-git-worktrees", "using-skills",
        "verification-before-completion", "writing-plans", "writing-skills",
    ];

  [Fact]
  public async Task Lists_AllTwentySkills_WithMetadata()
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

  [Fact]
  public async Task FlowTemplates_Carry_ExecutableLedgerContracts_InTheirOwnSections()
  {
    // The ledger only binds a claim to evidence if the flow's own dispatch templates
    // make the implementer record a prediction before experimenting and a verdict after,
    // and make the reviewer audit the ledger instead of trusting the report's prose.
    // Every assertion is scoped to the template that must carry it: a body-wide IndexOf
    // cannot tell "inside the implementer's contract block" from "inside the example
    // workflow", so a moved block would still pass.
    Result<SkillDefinition> r = await _catalog.GetAsync("experiment-driven-development", ct: TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess);
    string body = r.Value.Body;

    string implementer = FencedSection(body, "## Implementer Dispatch Template");
    // The tool's real parameter names, in backticks: it rejects unknown keys strictly, so
    // a described parameter invites {"reason": ...} and an InvalidParameterValue.
    Assert.Contains("`slug`", implementer, StringComparison.Ordinal);
    Assert.Contains("`id`", implementer, StringComparison.Ordinal);
    Assert.Contains("`text`", implementer, StringComparison.Ordinal);
    Assert.Contains("`falsifiedBy`", implementer, StringComparison.Ordinal);
    Assert.Contains("record-prediction", implementer, StringComparison.Ordinal);
    Assert.Contains("record-verdict", implementer, StringComparison.Ordinal);
    Assert.Contains("record-skip", implementer, StringComparison.Ordinal);
    // A prediction hard-requires an existing hypothesis id, so the template must say how
    // to establish one: without it the mandated call fails on first use, and the natural
    // recovery is the skip that bypasses the ledger.
    Assert.Contains("record-hypothesis", implementer, StringComparison.Ordinal);
    // A skip is a ledger record carrying an id, never a free-text reason: a report-only
    // skip leaves the ledger empty, and the finish gate reads empty as satisfied.
    Assert.Contains("skip <id> — <reason>", implementer, StringComparison.Ordinal);
    Assert.Contains("VERDICT: <in report:", implementer, StringComparison.Ordinal);
    // Ordering inside the template: prediction before the report, contract line after RED.
    Assert.True(implementer.IndexOf("record-prediction", StringComparison.Ordinal) <
        implementer.IndexOf("Write your FULL report", StringComparison.Ordinal),
        "the prediction requirement must precede the report paragraph");
    Assert.True(implementer.IndexOf("RED: <in report:", StringComparison.Ordinal) <
        implementer.IndexOf("VERDICT: <in report:", StringComparison.Ordinal),
        "the VERDICT line must follow the RED line in the returned contract");

    string reviewer = FencedSection(body, "## Task Reviewer Template");
    Assert.Contains("LEDGER AUDIT", reviewer, StringComparison.Ordinal);
    Assert.Contains("NEEDS_CONTEXT, not a", reviewer, StringComparison.Ordinal);
    // The audit names the skip row and the count the finish gate keys off, so a claimed
    // skip and an open prediction are both caught rather than trusted.
    Assert.Contains("skip <id>", reviewer, StringComparison.Ordinal);
    Assert.Contains("open prediction", reviewer, StringComparison.Ordinal);
    // Three numbered items mean three verdicts: framing and return line must agree, or a
    // reviewer can read item 3 as advisory and still believe it answered.
    Assert.Contains("Review for ALL THREE", reviewer, StringComparison.Ordinal);
    Assert.Contains("Return: all three verdicts", reviewer, StringComparison.Ordinal);
    Assert.True(reviewer.IndexOf("LEDGER AUDIT", StringComparison.Ordinal) >
        reviewer.IndexOf("2. TASK QUALITY", StringComparison.Ordinal),
        "the ledger audit must follow the task-quality item");
  }

  /// <summary>The fenced <c>text</c> block under a heading, fences excluded, so an
  ///     assertion cannot be satisfied by prose in another section.</summary>
  private static string FencedSection(string body, string heading)
  {
    int headingIndex = body.IndexOf(heading, StringComparison.Ordinal);
    Assert.True(headingIndex >= 0, $"missing section '{heading}'");
    int open = body.IndexOf("```text\n", headingIndex, StringComparison.Ordinal);
    Assert.True(open >= 0, $"missing the fenced block under '{heading}'");
    int contentStart = open + "```text\n".Length;
    int close = body.IndexOf("\n```", contentStart, StringComparison.Ordinal);
    Assert.True(close >= 0, $"unclosed fence under '{heading}'");
    return body[contentStart..close];
  }

  private static readonly (string Skill, string LegHeader, string Anchor)[] PipelineLegHeaders =
  [
      ("grill", "This is the observation leg of the scientific loop", "Interview the user relentlessly"),
      ("writing-plans", "This is the prediction leg of the scientific loop", "Write comprehensive implementation plans"),
      ("experiment-driven-development", "This carries the experiment and external-verdict legs", "Execute a plan by dispatching"),
      ("requesting-code-review", "This is the external-verdict leg", "Dispatch a code reviewer subagent"),
      ("receiving-code-review", "This is the analysis leg", "Code review requires technical evaluation"),
      ("reviewing-plans", "This is the external-verdict leg", "a verdict nobody can act on is not a verdict"),
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
    Assert.Contains("record-skip", body, StringComparison.Ordinal);
    Assert.Contains("An untested prediction is a belief, not a result", body, StringComparison.Ordinal);
    // The slug is read from the plan's own records, never guessed: a guessed slug reads
    // a different ledger and an empty one cannot be told from an absent one.
    Assert.Contains("Confirm the slug against the plan's own records", body, StringComparison.Ordinal);
    // Ordering, pinned positionally (the file's own precedent): a gate that drifted below
    // the continuation line would still contain every phrase above while blocking nothing.
    int gateIndex = body.IndexOf("### The ledger gate", StringComparison.Ordinal);
    int openPredictionIndex = body.IndexOf("open prediction", StringComparison.Ordinal);
    int continueIndex = body.IndexOf("Then continue to Step 2.", StringComparison.Ordinal);
    Assert.True(continueIndex >= 0, "missing the Step 2 continuation line");
    Assert.True(gateIndex < continueIndex, "the ledger gate must precede 'Then continue to Step 2.'");
    Assert.True(openPredictionIndex < continueIndex, "the open-prediction rule must precede 'Then continue to Step 2.'");
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
