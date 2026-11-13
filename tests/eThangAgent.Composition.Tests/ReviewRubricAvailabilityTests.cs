using eThangAgent.PlanDomain;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.Composition.Tests;
/// <summary>The verdict the plan-review skill publishes and the tokens the review record stores
///     are ONE list (issue #217). A conforming reviewer writes the skill's spelling and the
///     record parses it, so two spellings would mean a review that cannot be recorded.
///     <para>WHY THIS CHECK LIVES HERE AND NOT IN THE SKILL'S OWN TESTS: it needs the Plan
///     Domain's codec, and the Skill Domain's test project references only the Skill Domain.
///     The composition is the one place allowed to know both.</para>
///     <para>THE RUBRIC-TO-BRIEF CHECK THAT USED TO LIVE HERE IS GONE WITH THE REVIEW STEP.
///     A refiner applies the plan's own goal rather than a rubric, so there is no fact to flip
///     and no skill for a brief to name.</para></summary>
public class ReviewRubricAvailabilityTests
{

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
    int end = body.IndexOf('\n', start, StringComparison.Ordinal);
    Assert.True(end > start, "the contract block has no first line");
    return body[start..end];
  }
}
