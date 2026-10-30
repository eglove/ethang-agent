using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>Issue #320: the review back-edge against a fake store, a fake host, and a fake
///     review source. The verdict is no longer discarded, a denial moves the cursor BACK, and
///     the round-trip cap parks instead of looping.</summary>
public class ChainAdvanceReviewTests
{
  private static readonly DateTimeOffset T0 = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

  private sealed class FakeStore : IChainStore
  {
    private readonly Dictionary<int, Chain> _chains = [];
    private int _next = 1;

    public Task<Result<Chain>> CreateAsync(Chain draft, CancellationToken ct = default)
    {
      Chain saved = draft with { Id = _next++, Version = 1 };
      _chains[saved.Id] = saved;
      return Task.FromResult(Result.Success(saved));
    }

    public Task<Result<Chain>> GetAsync(int id, CancellationToken ct = default)
        => Task.FromResult(_chains.TryGetValue(id, out Chain? chain)
            ? Result.Success(chain)
            : Result.Failure<Chain>(new DomainError("ChainNotFound", $"no chain #{id}")));

    public Task<Result<ChainCatalog>> ListAsync(ChainStatus? status, CancellationToken ct = default)
        => Task.FromResult(Result.Success(new ChainCatalog(
            [.. _chains.Values.Where(c => status is null || c.Status == status)], [])));

    public Task<Result<Chain>> SaveAsync(Chain chain, int expectedVersion, CancellationToken ct = default)
    {
      if (!_chains.TryGetValue(chain.Id, out Chain? current))
      {
        return Task.FromResult(Result.Failure<Chain>(new DomainError("ChainNotFound", "gone")));
      }

      Chain saved = chain with { Version = current.Version + 1 };
      _chains[saved.Id] = saved;
      return Task.FromResult(Result.Success(saved));
    }

    public Task<Result<Chain?>> FindBySessionAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Success(_chains.Values.FirstOrDefault(chain => chain.Steps
            .Any(step => string.Equals(step.SessionId, sessionId, StringComparison.Ordinal)))));
  }

  private sealed class FakeHost : IChainAdvanceHost
  {
    public List<string> Closed { get; } = [];

    public List<ChainStepAssignment> Started { get; } = [];

    public Task CloseSessionAsync(string sessionId, CancellationToken ct = default)
    {
      Closed.Add(sessionId);
      return Task.CompletedTask;
    }

    public Task<Result<string>> StartSessionAsync(ChainStepAssignment assignment,
        string predecessorSessionId, CancellationToken ct = default)
    {
      Started.Add(assignment);
      return Task.FromResult(Result.Success("session-fix-" + assignment.Position));
    }
  }

  private sealed class FakeReviews : IChainReviewSource
  {
    public Dictionary<string, ChainReviewRecord> BySession { get; } =
        new(StringComparer.Ordinal);

    public List<ChainReviewRecord> Written { get; } = [];

    public Task<Result<bool>> RecordAsync(int planId, int planVersion, ChainReviewRecord review,
        string reviewerSessionId, CancellationToken ct = default)
    {
      Written.Add(review);
      BySession[reviewerSessionId] = review;
      return Task.FromResult(Result.Success(true));
    }

    public Task<Result<ChainReviewRecord?>> FindForSessionAsync(int planId,
        string reviewerSessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Success(BySession.TryGetValue(reviewerSessionId,
            out ChainReviewRecord? review) ? review : null));
  }

  private static T Must<T>(Result<T> result)
  {
    Assert.True(result.IsSuccess, result.Error?.Message ?? "expected success");
    return result.Value;
  }

  private static ChainReviewRecord Deny(params ChainReviewFinding[] findings) =>
      new(ChainReviewVerdict.Deny, findings);

  private static ChainReviewFinding Finding(string citation = "2") =>
      new("step 2 has no acceptance criterion", citation);

  /// <summary>A chain of implement(1), review(2), implement(3) with step 1 Done and step 2
  ///     claimed by the reviewer.</summary>
  private static async Task<(ChainAdvanceService Service, FakeStore Store, FakeHost Host,
      FakeReviews Reviews, Chain Chain)> NewReviewChainAsync()
  {
    FakeStore store = new();
    FakeHost host = new();
    FakeReviews reviews = new();
    ChainService chains = new(store);
    Chain chain = Must(await chains.CreateAsync(
        new ChainCreation("C:\\ws", 3, 1, "t", "session-root",
            [ChainStepSeed.Implementing(1),
                new ChainStepSeed(2, ChainStepRole.Review("vendor/reviewer")),
                ChainStepSeed.Implementing(3)]), T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    ChainAdvanceService service = new(chains, host, reviews: reviews);
    return (service, store, host, reviews, chain);
  }

  [Fact]
  public async Task Advance_OnDeny_ReopensTheJudgedStepAndStartsAFixSession()
  {
    (ChainAdvanceService service, FakeStore store, FakeHost host, FakeReviews reviews, Chain chain)
        = await NewReviewChainAsync().ConfigureAwait(true);
    ChainService chains = new(store);
    _ = Must(await chains.ClaimStepAsync(chain.Id, 1, "session-a", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    _ = Must(await chains.SettleStepAsync(chain.Id, 1, ChainStepStatus.Done,
        ChainActor.Session("session-a"), "implemented it", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    _ = Must(await chains.ClaimStepAsync(chain.Id, 2, "session-reviewer", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    reviews.BySession["session-reviewer"] = Deny(Finding("R2"));

    ChainAdvance decision = Must(await service.OnCompletionAsync("session-reviewer", planId: 3, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    // THE CURSOR MOVED BACK, not forward: the fix session is started for step 1, the step the
    // reviewer judged - not step 3, which is what a plain advance would have chosen.
    Assert.Equal(ChainAdvanceKind.StartStep, decision.Kind);
    Assert.Equal(1, decision.Position);
    ChainStepAssignment assignment = Assert.Single(host.Started);
    Assert.Equal(1, assignment.Position);

    Chain stored = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));
    ChainStep target = stored.StepAt(1)!;
    Assert.Equal(ChainStepStatus.Running, target.Status);
    Assert.Equal(1, target.ReviewAttempts);
    // THE VERDICT IS NO LONGER DISCARDED: the reopened step says why it is open again.
    Assert.Contains("step 2 has no acceptance criterion", target.Outcome, StringComparison.Ordinal);
    Assert.Contains("[cites: R2]", target.Outcome, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Advance_OnApprove_AdvancesPastTheReviewStep()
  {
    (ChainAdvanceService service, FakeStore store, FakeHost host, FakeReviews reviews, Chain chain)
        = await NewReviewChainAsync().ConfigureAwait(true);
    ChainService chains = new(store);
    _ = Must(await chains.ClaimStepAsync(chain.Id, 1, "session-a", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    _ = Must(await chains.SettleStepAsync(chain.Id, 1, ChainStepStatus.Done,
        ChainActor.Session("session-a"), null, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    _ = Must(await chains.ClaimStepAsync(chain.Id, 2, "session-reviewer", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    reviews.BySession["session-reviewer"] = new ChainReviewRecord(ChainReviewVerdict.Approve, []);

    ChainAdvance decision = Must(await service.OnCompletionAsync("session-reviewer", planId: 3, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Assert.Equal(ChainAdvanceKind.StartStep, decision.Kind);
    Assert.Equal(3, decision.Position);
    Assert.Equal(3, Assert.Single(host.Started).Position);
  }

  [Fact]
  public async Task Advance_OnApproveWithConditions_CarriesThemIntoTheSettledOutcome()
  {
    (ChainAdvanceService service, FakeStore store, _, FakeReviews reviews, Chain chain)
        = await NewReviewChainAsync().ConfigureAwait(true);
    ChainService chains = new(store);
    _ = Must(await chains.ClaimStepAsync(chain.Id, 1, "session-a", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    _ = Must(await chains.SettleStepAsync(chain.Id, 1, ChainStepStatus.Done,
        ChainActor.Session("session-a"), null, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    _ = Must(await chains.ClaimStepAsync(chain.Id, 2, "session-reviewer", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    reviews.BySession["session-reviewer"] = new ChainReviewRecord(
        ChainReviewVerdict.ApproveWithConditions, [Finding("R7")]);

    _ = Must(await service.OnCompletionAsync("session-reviewer", planId: 3, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Chain stored = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));
    // The conditions ride the settled step's outcome, which the next brief renders in its
    // predecessors section - the same path a denial's findings take.
    Assert.Contains("[cites: R7]", stored.StepAt(2)!.Outcome, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Advance_OnDenyAtTheCap_ParksTheChainNamingTheStep()
  {
    (ChainAdvanceService service, FakeStore store, FakeHost host, FakeReviews reviews, Chain chain)
        = await NewReviewChainAsync().ConfigureAwait(true);
    ChainService chains = new(store);
    _ = Must(await chains.ClaimStepAsync(chain.Id, 1, "session-a", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    _ = Must(await chains.SettleStepAsync(chain.Id, 1, ChainStepStatus.Done,
        ChainActor.Session("session-a"), null, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    // Spend the round-trip budget directly on the record: three sends back already made.
    for (int i = 0; i < ChainReviewPolicy.MaxReviewAttempts; i++)
    {
      _ = Must(await chains.ReopenStepAsync(chain.Id, 1, ChainActor.Harness, null, T0,
          TestContext.Current.CancellationToken).ConfigureAwait(true));
      _ = Must(await chains.ClaimStepAsync(chain.Id, 1, "session-a", T0,
          TestContext.Current.CancellationToken).ConfigureAwait(true));
      _ = Must(await chains.SettleStepAsync(chain.Id, 1, ChainStepStatus.Done,
          ChainActor.Session("session-a"), null, T0,
          TestContext.Current.CancellationToken).ConfigureAwait(true));
    }

    _ = Must(await chains.ClaimStepAsync(chain.Id, 2, "session-reviewer", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    reviews.BySession["session-reviewer"] = Deny(Finding());

    ChainAdvance decision = Must(await service.OnCompletionAsync("session-reviewer", planId: 3, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Assert.Equal(ChainAdvanceKind.Blocked, decision.Kind);
    Assert.Equal(ChainReviewCode.CapReached, decision.Code);
    Assert.Empty(host.Started);
    Chain stored = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));
    Assert.Equal(ChainStatus.Parked, stored.Status);
    Assert.Equal(1, stored.ParkedAtPosition);
    Assert.Contains("round-trip cap", stored.ParkReason, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Advance_OnAReviewStepWithNoVerdict_RefusesRatherThanDiscarding()
  {
    // THE DEFECT THIS CLOSES: the reviewer was told to record a verdict and the advance wrote
    // outcome: null. A review step that completes with no review is now refused by name.
    (ChainAdvanceService service, FakeStore store, FakeHost host, _, Chain chain)
        = await NewReviewChainAsync().ConfigureAwait(true);
    ChainService chains = new(store);
    _ = Must(await chains.ClaimStepAsync(chain.Id, 1, "session-a", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    _ = Must(await chains.SettleStepAsync(chain.Id, 1, ChainStepStatus.Done,
        ChainActor.Session("session-a"), null, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    _ = Must(await chains.ClaimStepAsync(chain.Id, 2, "session-reviewer", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    ChainAdvance decision = Must(await service.OnCompletionAsync("session-reviewer", planId: 3, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Assert.Equal(ChainAdvanceKind.Refused, decision.Kind);
    Assert.Equal(ChainReviewCode.NoVerdict, decision.Code);
    Assert.Contains("plan.review", decision.Reason, StringComparison.Ordinal);
    Assert.Empty(host.Started);
  }

  [Fact]
  public async Task Advance_OnAnImplementStep_ReadsNoReviewAtAll()
  {
    // An implement step must cost exactly what it did before #320: no review read, no branch.
    FakeStore store = new();
    FakeHost host = new();
    CountingReviews reviews = new();
    ChainService chains = new(store);
    Chain chain = Must(await chains.CreateAsync(ChainCreation.Implementing("C:\\ws", 3, 1, "t",
        "session-root", [1, 2]), T0, TestContext.Current.CancellationToken).ConfigureAwait(true));
    ChainAdvanceService service = new(chains, host, reviews: reviews);
    _ = Must(await chains.ClaimStepAsync(chain.Id, 1, "session-a", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    _ = Must(await service.OnCompletionAsync("session-a", planId: 3, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Assert.Equal(0, reviews.Reads);
    _ = Assert.Single(host.Started);
  }

  /// <summary>A review source that counts reads, so the implement-step path can be pinned to
  ///     zero.</summary>
  private sealed class CountingReviews : IChainReviewSource
  {
    public int Reads { get; private set; }

    public Task<Result<bool>> RecordAsync(int planId, int planVersion, ChainReviewRecord review,
        string reviewerSessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Success(true));

    public Task<Result<ChainReviewRecord?>> FindForSessionAsync(int planId,
        string reviewerSessionId, CancellationToken ct = default)
    {
      Reads++;
      return Task.FromResult(Result.Success<ChainReviewRecord?>(null));
    }
  }
}
