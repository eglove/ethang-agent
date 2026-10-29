using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.Skill.Domain.Tests;

/// <summary>The <c>reviewing-plans</c> skill (issue #217): the reviewer's prompt, rubric, and
///     verdict contract, shipped as an embedded skill.
///     <para>WHY THE CONTRACT IS PINNED RATHER THAN DESCRIBED: the brief a review step is
///     seeded with (#306) tells the reviewer to load this skill and apply its rubric, so the
///     skill IS the instruction the reviewer follows. A rubric that drifted, a verdict token
///     that changed spelling, or a citation rule that softened would each be invisible in the
///     brief and wrong in the verdict.</para>
///     <para>THE TOKEN CROSS-CHECK LIVES ELSEWHERE (Composition.Tests), because it needs the
///     Plan Domain's codec and this project references only the Skill Domain. What is pinned
///     here is the skill's own text; what is pinned there is that the text and the record
///     agree.</para></summary>
public class ReviewingPlansSkillTests
{
  private const string SkillName = "reviewing-plans";

  private static async Task<SkillDefinition> LoadAsync()
  {
    Result<SkillDefinition> r = await new EmbeddedSkillCatalog()
        .GetAsync(SkillName, ct: TestContext.Current.CancellationToken).ConfigureAwait(false);
    Assert.True(r.IsSuccess, r.Error?.Message);
    return r.Value;
  }

  [Fact]
  public async Task TheSkill_ShipsAsAnEmbeddedSkill_WithAReviewDescription()
  {
    // B1: no build-system change is needed because skills/** is already an embedded-resource
    // glob - so the only thing to prove is that the resource IS one and loads by name.
    SkillDefinition skill = await LoadAsync();

    Assert.Equal(SkillSource.BuiltIn, skill.Source);
    Assert.Contains("review a plan before implementation", skill.Description,
        StringComparison.OrdinalIgnoreCase);
    Assert.False(skill.Manual);
  }

  [Fact]
  public async Task TheOpening_NamesTheLoopLeg_BeforeTheInstructions()
  {
    // B6/D6: a review skill that does not say which loop leg it carries reads as another
    // checklist, and the router's table (scientific-method) would name a leg no skill claims.
    SkillDefinition skill = await LoadAsync();

    int leg = skill.Body.IndexOf("external-verdict leg", StringComparison.Ordinal);
    int firstHeading = skill.Body.IndexOf("## ", StringComparison.Ordinal);

    Assert.True(leg >= 0, "the opening must name the loop leg it carries");
    Assert.True(firstHeading > leg, "the leg must be stated in the opening, not buried below a heading");
  }

  [Fact]
  public async Task TheInputs_AreThePlanTheRequirementsAndTheRubric_AndNeverThePlannersHistory()
  {
    // B2/D2: the code-review skill's rule, applied harder here. A reviewer that reads the
    // planner's reasoning inherits the planner's framing, which is the self-grading the
    // external verdict exists to prevent.
    SkillDefinition skill = await LoadAsync();

    Assert.Contains("**The plan**", skill.Body, StringComparison.Ordinal);
    Assert.Contains("**The requirements**", skill.Body, StringComparison.Ordinal);
    Assert.Contains("**The rubric**", skill.Body, StringComparison.Ordinal);
    Assert.Contains("Never read the planner's session history", skill.Body, StringComparison.Ordinal);
  }

  [Fact]
  public async Task TheOutputContract_PutsTheVerdictLineFirst_WithExactlyThreeTokens()
  {
    // B3/D3: the chain cursor parses the verdict before anything else, so a machine-readable
    // first line is what keeps the contract cheap. The three spellings are the record's
    // (PlanReviewVerdictCodec.Tokens); this pins that the skill publishes them.
    string contract = Contract(await LoadAsync());

    Assert.StartsWith("verdict:", contract, StringComparison.Ordinal);
    Assert.Contains("approve | deny | approve-with-conditions", contract, StringComparison.Ordinal);
  }

  [Fact]
  public async Task TheRubric_ScoresExactlyThreeDimensions_EachWithAJustification()
  {
    // B4/D4: three named dimensions are enough to compare reviews and few enough to score
    // consistently; an unjustified score is an opinion with a number attached.
    SkillDefinition skill = await LoadAsync();
    string contract = Contract(skill);

    Assert.Contains("requirement-coverage", contract, StringComparison.Ordinal);
    Assert.Contains("testability", contract, StringComparison.Ordinal);
    Assert.Contains("blast-radius", contract, StringComparison.Ordinal);
    Assert.Contains("one-line justification", skill.Body, StringComparison.Ordinal);
    // The scale is the skill's, and the skill must SAY so: the review record enforces only
    // non-negativity, so a reviewer that invents a scale produces an incomparable review.
    Assert.Contains("The scale is this skill's, not the record's", skill.Body, StringComparison.Ordinal);
  }

  [Fact]
  public async Task TheCitationRule_IsStated_AndAnUncitedFindingIsARedFlag()
  {
    // B5/D5: 'the plan is vague' cannot be acted on or audited; 'step 3 has no acceptance
    // criterion for requirement R2' can. The rule is enforced at write time by the record
    // (#216), so the skill has to state it or a conforming reviewer is refused.
    SkillDefinition skill = await LoadAsync();

    Assert.Contains("**Every finding cites a requirement id or a step position.**", skill.Body,
        StringComparison.Ordinal);
    Assert.Contains("**A finding with no citation.**", skill.Body, StringComparison.Ordinal);
  }

  [Fact]
  public async Task TheSkill_StatesWhatEachVerdictDoes_BecauseTheReviewerIsAChainStep()
  {
    // B7/D7: a reviewer that does not know its verdict has consequences writes a verdict
    // shaped for a human instead of for the harness.
    SkillDefinition skill = await LoadAsync();

    Assert.Contains("The chain advances and the plan is executed as written.", skill.Body,
        StringComparison.Ordinal);
    Assert.Contains("A fix session starts, seeded with your findings and their citations", skill.Body,
        StringComparison.Ordinal);
    Assert.Contains("the conditions travel into the next step's brief", skill.Body,
        StringComparison.Ordinal);
  }

  [Fact]
  public async Task TheSkill_ForbidsFixingWhatItReviews()
  {
    // The review brief (#306) states this too, and the duplication is deliberate: the brief
    // is a seeded message a compaction can evict, while the skill is loaded on demand.
    SkillDefinition skill = await LoadAsync();

    Assert.Contains("**Fixing what you review.**", skill.Body, StringComparison.Ordinal);
  }

  /// <summary>The fenced <c>text</c> block under the output-contract heading, fences
  ///     excluded, so an assertion cannot be satisfied by prose in another section.</summary>
  private static string Contract(SkillDefinition skill)
  {
    const string heading = "## The output contract";
    int at = skill.Body.IndexOf(heading, StringComparison.Ordinal);
    Assert.True(at >= 0, "missing the output-contract section");
    int open = skill.Body.IndexOf("```text\n", at, StringComparison.Ordinal);
    Assert.True(open >= 0, "missing the fenced contract block");
    int start = open + "```text\n".Length;
    int close = skill.Body.IndexOf("\n```", start, StringComparison.Ordinal);
    Assert.True(close >= 0, "unclosed contract fence");
    return skill.Body[start..close];
  }
}
