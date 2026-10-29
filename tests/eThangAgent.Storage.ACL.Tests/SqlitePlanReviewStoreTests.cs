using eThangAgent.PlanDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Issue #216: a review round-trips through the store with every field intact.
///     <para>WORKSPACE ISOLATION IS BY OWNERSHIP: the table carries no workspace column, so
///     a review is visible only through a plan the workspace owns. The isolation test is
///     therefore about PLANS, not about a duplicated column that does not exist.</para></summary>
public sealed class SqlitePlanReviewStoreTests : IDisposable
{
  private const string Ws = "C:\\ws\\alpha";
  private readonly string _dbPath = Path.Combine(
      Path.GetTempPath(), $"ethang-planreview-{Guid.NewGuid():N}.db");
  private readonly AppDatabase _database;
  private readonly SqlitePlanStore _plans;
  private readonly SqlitePlanReviewStore _reviews;

  public SqlitePlanReviewStoreTests()
  {
    _database = new AppDatabase(_dbPath);
    _plans = new SqlitePlanStore(_database, Ws);
    _reviews = new SqlitePlanReviewStore(_database, Ws);
  }

  public void Dispose()
  {
    GC.SuppressFinalize(this);
    // Named decision (CA1031): temp-db cleanup is best effort.
#pragma warning disable CA1031, S108 // Do not catch general exception types
    try
    {
      File.Delete(_dbPath);
    }
    catch { }
#pragma warning restore CA1031, S108
  }

  private async Task<Plan> CreatePlanAsync(string title = "Ship it")
  {
    Result<Plan> created = await _plans.CreateAsync(
        Plan.New(title, "goal", "sess-planner", DateTimeOffset.UtcNow)
            .AddStep("one", null).AddStep("two", "detail"),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(created.IsSuccess, created.Error?.Message ?? "expected a plan");
    return created.Value;
  }

  private static PlanReview Review(int planId, int planVersion = 1,
      PlanReviewVerdict verdict = PlanReviewVerdict.Deny,
      IReadOnlyList<PlanReviewFinding>? findings = null) =>
      PlanReview.New(new PlanReview(0, planId, planVersion, verdict, "sess-reviewer",
          "vendor/reviewer", new PlanReviewScores(4, 3, 2),
          findings ?? [new PlanReviewFinding("step 3 has no acceptance criterion", "R2")],
          "no rubric was installed", DateTimeOffset.UtcNow));

  [Fact]
  public async Task Create_RoundTripsEveryField()
  {
    Plan plan = await CreatePlanAsync();
    PlanReview draft = Review(plan.Id, planVersion: 1, verdict: PlanReviewVerdict.ApproveWithConditions,
        findings: [
          new PlanReviewFinding("no acceptance criterion", "R2"),
          new PlanReviewFinding("step 4 is untestable", "step 4"),
        ]);

    Result<PlanReview> created = await _reviews.CreateAsync(
        draft, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(created.IsSuccess, created.Error?.Message ?? "expected the review to save");
    Assert.True(created.Value.Id > 0);

    Result<IReadOnlyList<PlanReview>> listed = await _reviews.ListForPlanAsync(
        plan.Id, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(listed.IsSuccess, listed.Error?.Message ?? "expected a listing");
    PlanReview back = Assert.Single(listed.Value);
    Assert.Equal(plan.Id, back.PlanId);
    Assert.Equal(1, back.PlanVersion);
    Assert.Equal(PlanReviewVerdict.ApproveWithConditions, back.Verdict);
    Assert.Equal("sess-reviewer", back.ReviewerSessionId);
    Assert.Equal("vendor/reviewer", back.ReviewerModel);
    Assert.Equal(4, back.Scores.RequirementCoverage);
    Assert.Equal(3, back.Scores.Testability);
    Assert.Equal(2, back.Scores.BlastRadius);
    Assert.Equal("no rubric was installed", back.Notes);
    Assert.Equal(2, back.Findings.Count);
    Assert.Equal("R2", back.Findings[0].Citation);
    Assert.Equal("step 4", back.Findings[1].Citation);
  }

  [Fact]
  public async Task Create_RefusesAnUnknownPlan_WithATypedError()
  {
    Result<PlanReview> refused = await _reviews.CreateAsync(
        Review(4242), TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(refused.IsSuccess, "a review of a plan that does not exist must be refused");
    Assert.Equal("PlanNotFound", refused.Error.Code);
  }

  [Fact]
  public async Task Create_RefusesAnUncitedFinding_AtWriteTime()
  {
    Plan plan = await CreatePlanAsync();
    PlanReview uncited = Review(plan.Id, findings: [new PlanReviewFinding("vague", "")]);

    Result<PlanReview> refused = await _reviews.CreateAsync(
        uncited, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(refused.IsSuccess, "an uncited finding must never reach the database");
    Assert.Equal("UncitedFinding", refused.Error.Code);

    Result<IReadOnlyList<PlanReview>> listed = await _reviews.ListForPlanAsync(
        plan.Id, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.Empty(listed.Value!);
  }

  [Fact]
  public async Task List_ReturnsCreationOrder_WithEachJudgedVersion()
  {
    Plan plan = await CreatePlanAsync();
    _ = await _reviews.CreateAsync(Review(plan.Id, planVersion: 1),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = await _reviews.CreateAsync(Review(plan.Id, planVersion: 4),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = await _reviews.CreateAsync(Review(plan.Id, planVersion: 7),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Result<IReadOnlyList<PlanReview>> listed = await _reviews.ListForPlanAsync(
        plan.Id, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(listed.IsSuccess);
    Assert.Equal([1, 4, 7], listed.Value.Select(r => r.PlanVersion));
  }

  [Fact]
  public async Task List_ForAPlanWithNoReviews_IsEmpty_NotAFailure()
  {
    Plan plan = await CreatePlanAsync();
    Result<IReadOnlyList<PlanReview>> listed = await _reviews.ListForPlanAsync(
        plan.Id, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(listed.IsSuccess, "'no reviews' is an answer, not a failure");
    Assert.Empty(listed.Value);
  }

  [Fact]
  public async Task Latest_ReturnsTheNewestReview()
  {
    Plan plan = await CreatePlanAsync();
    _ = await _reviews.CreateAsync(Review(plan.Id, verdict: PlanReviewVerdict.Deny),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = await _reviews.CreateAsync(Review(plan.Id, verdict: PlanReviewVerdict.Approve),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Result<PlanReview?> latest = await _reviews.LatestForPlanAsync(
        plan.Id, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(latest.IsSuccess);
    PlanReview? newest = latest.Value;
    Assert.True(newest is not null, "the plan has two reviews");
    Assert.Equal(PlanReviewVerdict.Approve, newest.Verdict);
  }

  [Fact]
  public async Task Latest_ForAPlanWithNoReviews_IsNull()
  {
    Plan plan = await CreatePlanAsync();
    Result<PlanReview?> latest = await _reviews.LatestForPlanAsync(
        plan.Id, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(latest.IsSuccess);
    Assert.True(latest.Value is null, "a plan nobody reviewed has no verdict");
  }

  [Fact]
  public async Task Isolation_AnotherWorkspaceCannotSeeTheReview()
  {
    Plan plan = await CreatePlanAsync();
    _ = await _reviews.CreateAsync(Review(plan.Id),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    SqlitePlanReviewStore other = new(_database, "C:\\ws\\beta");
    Result<IReadOnlyList<PlanReview>> listed = await other.ListForPlanAsync(
        plan.Id, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(listed.IsSuccess);
    Assert.Empty(listed.Value);
  }

  [Fact]
  public async Task Isolation_AnotherWorkspaceCannotReviewThePlan()
  {
    Plan plan = await CreatePlanAsync();
    SqlitePlanReviewStore other = new(_database, "C:\\ws\\beta");
    Result<PlanReview> refused = await other.CreateAsync(
        Review(plan.Id), TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(refused.IsSuccess);
    Assert.Equal("PlanNotFound", refused.Error.Code);
  }

  [Fact]
  public async Task Create_KeepsTwoReviewsOfDifferentVersionsDistinct()
  {
    Plan plan = await CreatePlanAsync();
    _ = await _reviews.CreateAsync(Review(plan.Id, planVersion: 2, verdict: PlanReviewVerdict.Approve),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = await _reviews.CreateAsync(Review(plan.Id, planVersion: 5, verdict: PlanReviewVerdict.Deny),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    // D1's whole point: a stale approve must not cover content nobody reviewed.
    Result<IReadOnlyList<PlanReview>> listed = await _reviews.ListForPlanAsync(
        plan.Id, TestContext.Current.CancellationToken).ConfigureAwait(true);
    IReadOnlyList<PlanReview> reviews = listed.Value!;
    Assert.Equal(2, reviews.Count);
    Assert.Equal(PlanReviewVerdict.Approve, reviews[0].Verdict);
    Assert.Equal(2, reviews[0].PlanVersion);
    Assert.Equal(PlanReviewVerdict.Deny, reviews[1].Verdict);
    Assert.Equal(5, reviews[1].PlanVersion);
  }
}
