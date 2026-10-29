using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.PlanDomain;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>The rubric a review step is told to apply is the <c>reviewing-plans</c> skill
///     (issue #217), and TWO things must hold now that it ships:
///     <list type="number">
///     <item>the brief's rubric fact flips to true for a review step with NO code change —
///     #306 wired <see cref="ChainBriefFacts.ReviewRubricAvailable"/> to the same catalog the
///     skill listing is built from precisely so that shipping the skill would be the only
///     step, and a claim like that is worth asserting rather than believing;</item>
///     <item>the verdict the skill publishes and the tokens the review record stores are ONE
///     list. A conforming reviewer writes the skill's spelling, and the record parses it —
///     two spellings would mean a review that cannot be recorded.</item>
///     </list>
///     <para>WHY THE SECOND CHECK LIVES HERE AND NOT IN THE SKILL'S OWN TESTS: it needs the
///     Plan Domain's codec, and the Skill Domain's test project references only the Skill
///     Domain. The composition is the one place allowed to know both.</para></summary>
public class ReviewRubricAvailabilityTests
{
  private static ServiceProvider Build()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new SubAgentOptions(null, 2));
    return new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", 512, 0.5f, 8192).Value!,
            new AgentHostOptions(
                FixedWorkspaceContext.ForPath("app"), new UnrootedPathResolver()), db.Database)
        .BuildServiceProvider();
  }

  [Fact]
  public async Task AReviewStep_IsToldToApplyTheReviewingPlansRubric()
  {
    // The fact is answered by the REAL source over the REAL catalog, so this fails if the
    // skill stops shipping, if its name drifts, or if the catalog the brief asks is no
    // longer the one the listing is built from.
    using ServiceProvider services = Build();
    IChainBriefSource source = services.GetRequiredService<IChainBriefSource>();

    Result<bool> available = await source.GetReviewRubricAvailableAsync(
        TestContext.Current.CancellationToken);

    Assert.True(available.IsSuccess, available.Error?.Message);
    Assert.True(available.Value, "the reviewing-plans skill ships, so a review step has a rubric");

    // And the brief RENDERS that answer as the instruction, not as an absence.
    Result<ChainBrief> brief = ChainBriefRenderer.Render(Facts(rubricAvailable: available.Value));
    Assert.True(brief.IsSuccess, brief.Error?.Message);
    Assert.Contains("Load the `reviewing-plans` skill", brief.Value.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("No `reviewing-plans` skill is installed", brief.Value.Content,
        StringComparison.Ordinal);
  }

  [Fact]
  public async Task TheSkillsVerdictTokens_AreExactlyTheTokensTheReviewRecordStores()
  {
    // The skill publishes the spellings a reviewer writes; PlanReviewVerdictCodec decodes
    // them at write time. If these two lists ever differ, a reviewer that followed the skill
    // perfectly produces a verdict the record refuses - so the seam is pinned, not assumed.
    Result<SkillDefinition> skill = await new EmbeddedSkillCatalog()
        .GetAsync("reviewing-plans", ct: TestContext.Current.CancellationToken);
    Assert.True(skill.IsSuccess, skill.Error?.Message);

    string published = VerdictLine(skill.Value.Body);
    string[] tokens = [.. PlanReviewVerdictCodec.Tokens];

    Assert.Equal("verdict: " + string.Join(" | ", tokens), published);
  }

  [Fact]
  public async Task EveryTokenTheSkillPublishes_DecodesToADistinctVerdict()
  {
    // The other direction: a token the skill publishes that the codec cannot decode is the
    // same defect seen from the reviewer's side, and a codec that mapped two tokens to one
    // verdict would make approve and deny indistinguishable on the record.
    Result<SkillDefinition> skill = await new EmbeddedSkillCatalog()
        .GetAsync("reviewing-plans", ct: TestContext.Current.CancellationToken);
    Assert.True(skill.IsSuccess, skill.Error?.Message);

    List<PlanReviewVerdict> decoded = [];
    foreach (string token in PlanReviewVerdictCodec.Tokens)
    {
      Result<PlanReviewVerdict> verdict = PlanReviewVerdictCodec.Decode(token);
      Assert.True(verdict.IsSuccess, token);
      decoded.Add(verdict.Value);
    }

    Assert.Equal(PlanReviewVerdictCodec.Tokens.Count, decoded.Distinct().Count());
    Assert.Contains("verdict: " + string.Join(" | ", PlanReviewVerdictCodec.Tokens),
        skill.Value.Body, StringComparison.Ordinal);
  }

  /// <summary>The skill's published verdict line, read from the fenced output-contract block
  ///     so a stray mention elsewhere in the body cannot satisfy the assertion.</summary>
  private static string VerdictLine(string body)
  {
    int at = body.IndexOf("## The output contract", StringComparison.Ordinal);
    Assert.True(at >= 0, "missing the output-contract section");
    int open = body.IndexOf("```text\n", at, StringComparison.Ordinal);
    Assert.True(open >= 0, "missing the fenced contract block");
    int start = open + "```text\n".Length;
    int end = body.IndexOf('\n', start);
    Assert.True(end > start, "the contract block has no first line");
    return body[start..end];
  }

  /// <summary>A review step's facts, with the rubric answer supplied by the caller so the
  ///     render assertion reads the REAL catalog's answer rather than a hard-coded true.</summary>
  private static ChainBriefFacts Facts(bool rubricAvailable) => new(
      ChainId: 7,
      ChainTitle: "2026-08-24-dashboard-truth",
      PlanId: 104,
      PlanVersion: 3,
      PlanGoal: "Make the dashboard tell the truth about what it did.",
      AssignedPosition: 2,
      PlanSteps: [new ChainBriefPlanStep(1, "Honest status line", null),
          new ChainBriefPlanStep(2, "Judge the plan", "Review it.")],
      Predecessors: [],
      GateCommands: ["dotnet build"],
      LedgerKey: "sci.dashboard-truth/ledger",
      Role: ChainStepRole.Review("vendor/reviewer"),
      ReviewRubricAvailable: rubricAvailable);
}
