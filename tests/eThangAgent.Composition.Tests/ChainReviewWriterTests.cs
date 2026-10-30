using eThangAgent.AgentDomain;
using eThangAgent.PlanDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;

namespace eThangAgent.Composition.Tests;

/// <summary>Issue #320: the review WRITER end to end - <c>plan.review</c> records a verdict
///     into <c>plan_reviews</c>, the row round-trips with its plan id, judged version, verdict,
///     scores, findings, reviewer session and model, and the chain cursor reads back exactly
///     what was written.
///     <para>THIS IS THE DEFECT THE ISSUE EXISTS FOR: before this, no <c>src</c> file wrote a
///     review row at all, so a reviewer did the work and nothing kept it.</para></summary>
public sealed class ChainReviewWriterTests : IDisposable
{
  private const string Ws = "C:\\ws\\review";
  private readonly string _dbPath = Path.Combine(
      Path.GetTempPath(), $"ethang-reviewwriter-{Guid.NewGuid():N}.db");

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

  /// <summary>Seeds one Active plan at id 7, because a review is a child of a plan and the
  ///     foreign key refuses a review of one that does not exist.</summary>
  private async Task<int> SeedPlanAsync()
  {
    AppDatabase database = new(_dbPath);
    SqlitePlanStore plans = new(database, Ws);
    Plan plan = Plan.New("Ship it", "make it true", "session-planner", DateTimeOffset.UtcNow)
        .AddStep("implement", null)
        .AddStep("review", null);
    Result<Plan> created = await plans.CreateAsync(plan, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.True(created.IsSuccess, created.Error?.Message ?? "fixture plan must be created");
    return created.Value.Id;
  }

  private static T Must<T>(Result<T> result)
  {
    Assert.True(result.IsSuccess, result.Error?.Message ?? "expected success");
    return result.Value;
  }

  [Fact]
  public async Task RecordAsync_WritesTheReviewRow_WithEveryFieldItPromised()
  {
    int planId = await SeedPlanAsync().ConfigureAwait(true);
    AppDatabase database = new(_dbPath);
    SqlitePlanReviewStore reviews = new(database, Ws);
    CompositionChainReviewSource writer = new(reviews);
    ChainReviewRecord review = new(
        ChainReviewVerdict.Deny,
        [new ChainReviewFinding("step 3 cites no requirement", "R2")],
        new ChainReviewScores(4, 2, 3),
        "vendor/reviewer");

    _ = Must(await writer.RecordAsync(planId, planVersion: 3, review,
        "session-reviewer", TestContext.Current.CancellationToken).ConfigureAwait(true));

    IReadOnlyList<PlanReview> stored = Must(await reviews
        .ListForPlanAsync(planId, TestContext.Current.CancellationToken).ConfigureAwait(true));
    PlanReview row = Assert.Single(stored);
    Assert.Equal(planId, row.PlanId);
    Assert.Equal(3, row.PlanVersion);
    Assert.Equal(PlanReviewVerdict.Deny, row.Verdict);
    Assert.Equal(4, row.Scores.RequirementCoverage);
    Assert.Equal(2, row.Scores.Testability);
    Assert.Equal(3, row.Scores.BlastRadius);
    Assert.Equal("session-reviewer", row.ReviewerSessionId);
    Assert.Equal("vendor/reviewer", row.ReviewerModel);
    PlanReviewFinding finding = Assert.Single(row.Findings);
    Assert.Equal("step 3 cites no requirement", finding.Text);
    Assert.Equal("R2", finding.Citation);
  }

  [Fact]
  public async Task FindForSessionAsync_ReturnsWhatTheCursorNeeds()
  {
    // THE CURSOR'S ONE READ: the same rows the writer wrote, read back through the port.
    int planId = await SeedPlanAsync().ConfigureAwait(true);
    AppDatabase database = new(_dbPath);
    SqlitePlanReviewStore reviews = new(database, Ws);
    CompositionChainReviewSource writer = new(reviews);
    _ = Must(await writer.RecordAsync(planId, 3,
        new ChainReviewRecord(ChainReviewVerdict.Deny,
            [new ChainReviewFinding("no criterion", "step 3")], ReviewerModel: "vendor/reviewer"),
        "session-reviewer", TestContext.Current.CancellationToken).ConfigureAwait(true));

    ChainReviewRecord? found = Must(await writer.FindForSessionAsync(planId, "session-reviewer",
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Assert.NotNull(found);
    Assert.True(found.IsDenial);
    ChainReviewFinding finding = Assert.Single(found.Findings);
    Assert.Equal("step 3", finding.Citation);
  }

  [Fact]
  public async Task FindForSessionAsync_ReturnsNull_ForASessionThatReviewedNothing()
  {
    AppDatabase database = new(_dbPath);
    CompositionChainReviewSource writer = new(new SqlitePlanReviewStore(database, Ws));

    ChainReviewRecord? found = Must(await writer.FindForSessionAsync(7, "session-other",
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Assert.Null(found);
  }

  [Fact]
  public async Task RecordAsync_RefusesAnUncitedFinding_AndWritesNothing()
  {
    // The record's own write gate, reached through the writer: an uncited finding is refused
    // before it reaches the database, and the refusal names which finding it was.
    int planId = await SeedPlanAsync().ConfigureAwait(true);
    AppDatabase database = new(_dbPath);
    SqlitePlanReviewStore reviews = new(database, Ws);
    CompositionChainReviewSource writer = new(reviews);
    ChainReviewRecord review = new(ChainReviewVerdict.Approve,
        [new ChainReviewFinding("the code is unclear", "  ")], ReviewerModel: "vendor/reviewer");

    Result<bool> refused = await writer.RecordAsync(planId, 1, review, "session-reviewer",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(refused.IsSuccess);
    Assert.Equal("UncitedFinding", refused.Error.Code);
    Assert.Empty(Must(await reviews.ListForPlanAsync(planId, TestContext.Current.CancellationToken)
        .ConfigureAwait(true)));
  }

  [Fact]
  public async Task RecordAsync_RefusesAPlanThatDoesNotExist()
  {
    AppDatabase database = new(_dbPath);
    CompositionChainReviewSource writer = new(new SqlitePlanReviewStore(database, Ws));

    Result<bool> refused = await writer.RecordAsync(planId: 999, planVersion: 1,
        new ChainReviewRecord(ChainReviewVerdict.Approve, [], ReviewerModel: "vendor/reviewer"),
        "session-reviewer",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(refused.IsSuccess);
    Assert.Equal("PlanNotFound", refused.Error.Code);
  }
}
