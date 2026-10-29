using eThangAgent.SharedKernel;
using Xunit;

namespace eThangAgent.PlanDomain.Tests;

/// <summary>Issue #216: the review record's write-time gate. Every acceptance criterion
///     that is about REJECTION lives here - an uncited finding, a negative score, a missing
///     provenance field, an out-of-enum verdict - because the store's own tests can only
///     prove the refusal reaches SQLite, not that the rule is right.</summary>
public class PlanReviewTests
{
  private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

  private static PlanReviewScores Scores(int coverage = 4, int testability = 3, int blast = 2) =>
      new(coverage, testability, blast);

  private static PlanReview Draft(
      PlanReviewVerdict verdict = PlanReviewVerdict.Approve,
      IReadOnlyList<PlanReviewFinding>? findings = null,
      string? session = "sess-reviewer",
      string? model = "vendor/reviewer",
      PlanReviewScores? scores = null,
      int planId = 7,
      int planVersion = 3) =>
      PlanReview.New(new PlanReview(0, planId, planVersion, verdict, session, model,
          scores ?? Scores(), findings ?? [], null, Now));

  [Fact]
  public void Validate_AcceptsACompleteReview()
  {
    Result<PlanReview> valid = Draft().Validate();
    Assert.True(valid.IsSuccess, valid.Error?.Message ?? "expected a valid review");
  }

  [Fact]
  public void Validate_AcceptsAnApproveWithNoFindings()
  {
    Result<PlanReview> valid = Draft(findings: []).Validate();
    Assert.True(valid.IsSuccess, valid.Error?.Message ?? "a clean approve carries no findings");
  }

  [Theory]
  [InlineData("approve")]
  [InlineData("deny")]
  [InlineData("approve-with-conditions")]
  public void Validate_AcceptsEveryVerdict(string token)
  {
    PlanReviewVerdict verdict = PlanReviewVerdictCodec.Decode(token).Value;
    Result<PlanReview> valid = Draft(verdict: verdict).Validate();
    Assert.True(valid.IsSuccess, valid.Error?.Message ?? "expected every verdict to be recordable");
  }

  [Fact]
  public void Validate_RefusesAnOutOfEnumVerdict_WithATypedError()
  {
    Result<PlanReview> invalid = Draft(verdict: (PlanReviewVerdict)99).Validate();
    Assert.False(invalid.IsSuccess, "an undefined verdict must not be recordable");
    Assert.Equal("UnknownVerdict", invalid.Error.Code);
  }

  [Fact]
  public void Validate_RefusesAFindingWithoutACitation()
  {
    PlanReview review = Draft(findings: [new PlanReviewFinding("the plan is vague", "   ")]);
    Result<PlanReview> invalid = review.Validate();
    Assert.False(invalid.IsSuccess, "an uncited finding must be rejected at write time");
    Assert.Equal("UncitedFinding", invalid.Error.Code);
    Assert.Contains("finding 1", invalid.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Validate_NamesTheOffendingFindingIndex()
  {
    PlanReview review = Draft(findings: [
      new PlanReviewFinding("fine", "R1"),
      new PlanReviewFinding("also fine", "step 2"),
      new PlanReviewFinding("uncited", ""),
    ]);
    Result<PlanReview> invalid = review.Validate();
    Assert.False(invalid.IsSuccess);
    Assert.Contains("finding 3", invalid.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Validate_RefusesAFindingThatSaysNothing()
  {
    PlanReview review = Draft(findings: [new PlanReviewFinding("  ", "R1")]);
    Result<PlanReview> invalid = review.Validate();
    Assert.False(invalid.IsSuccess);
    Assert.Equal("UncitedFinding", invalid.Error.Code);
  }

  [Theory]
  [InlineData(-1, 3, 2)]
  [InlineData(4, -1, 2)]
  [InlineData(4, 3, -1)]
  public void Validate_RefusesANegativeRubricScore(int coverage, int testability, int blast)
  {
    Result<PlanReview> invalid = Draft(scores: Scores(coverage, testability, blast)).Validate();
    Assert.False(invalid.IsSuccess, "a negative rubric score must be refused");
    Assert.Equal("InvalidRubricScore", invalid.Error.Code);
  }

  [Fact]
  public void Validate_AcceptsAZeroScore_WhichIsARealScore()
  {
    Result<PlanReview> valid = Draft(scores: Scores(0, 0, 0)).Validate();
    Assert.True(valid.IsSuccess, "zero is a measurement, not an absence");
  }

  [Fact]
  public void Validate_RefusesAMissingReviewerSession()
  {
    Result<PlanReview> invalid = Draft(session: null).Validate();
    Assert.False(invalid.IsSuccess, "a verdict with no provenance is not recordable");
    Assert.Equal("MissingReviewerSession", invalid.Error.Code);
  }

  [Fact]
  public void Validate_RefusesAMissingReviewerModel()
  {
    Result<PlanReview> invalid = Draft(model: "  ").Validate();
    Assert.False(invalid.IsSuccess);
    Assert.Equal("MissingReviewerModel", invalid.Error.Code);
  }

  [Fact]
  public void Validate_RefusesAnUnassignedPlanId()
  {
    Result<PlanReview> invalid = Draft(planId: 0).Validate();
    Assert.False(invalid.IsSuccess);
    Assert.Equal("PlanNotFound", invalid.Error.Code);
  }

  [Fact]
  public void Validate_RefusesAnUnassignedPlanVersion()
  {
    Result<PlanReview> invalid = Draft(planVersion: 0).Validate();
    Assert.False(invalid.IsSuccess);
    Assert.Equal("InvalidPlanVersion", invalid.Error.Code);
  }

  [Theory]
  [InlineData(PlanReviewVerdict.Approve, true)]
  [InlineData(PlanReviewVerdict.ApproveWithConditions, true)]
  [InlineData(PlanReviewVerdict.Deny, false)]
  public void IsApproval_IsTrueForBothApprovingVerdicts(PlanReviewVerdict verdict, bool expected) =>
    Assert.Equal(expected, Draft(verdict: verdict).IsApproval);

  [Fact]
  public void New_AssignsAnUnassignedId_AndKeepsTheJudgedVersion()
  {
    PlanReview draft = Draft(planVersion: 9);
    Assert.Equal(0, draft.Id);
    Assert.Equal(9, draft.PlanVersion);
    Assert.Equal(7, draft.PlanId);
  }

  [Fact]
  public void FindingCreate_TrimsBothHalves()
  {
    Result<PlanReviewFinding> created = PlanReviewFinding.Create("  vague step  ", "  R2  ");
    Assert.True(created.IsSuccess, created.Error?.Message ?? "expected a finding");
    Assert.Equal("vague step", created.Value.Text);
    Assert.Equal("R2", created.Value.Citation);
  }

  [Fact]
  public void FindingCreate_TreatsNullAsBlank_NotAsAProgrammerError()
  {
    Result<PlanReviewFinding> created = PlanReviewFinding.Create(null, null);
    Assert.False(created.IsSuccess, "a finding with no text and no citation is not a finding");
    Assert.Equal("UncitedFinding", created.Error.Code);
  }

  [Fact]
  public void ScoresCreate_RefusesEachNegativeDimensionByName()
  {
    Assert.Contains("requirement coverage",
        PlanReviewScores.Create(-1, 0, 0).Error?.Message ?? "", StringComparison.Ordinal);
    Assert.Contains("testability",
        PlanReviewScores.Create(0, -1, 0).Error?.Message ?? "", StringComparison.Ordinal);
    Assert.Contains("blast radius",
        PlanReviewScores.Create(0, 0, -1).Error?.Message ?? "", StringComparison.Ordinal);
  }
}
