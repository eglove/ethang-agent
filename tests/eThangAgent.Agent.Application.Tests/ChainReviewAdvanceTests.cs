using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>The review back-edge: a verdict on IMPLEMENTATION moves the chain. A pass advances past
///     the review step, a fail reopens the step it judged for a fix session seeded with the cited
///     findings, and the cap parks rather than looping.
///     <para>THE POINT OF THESE TESTS is that the verdict is read from the RECORD, not from the
///     reviewer's prose: the reviewer's session closes the moment its step settles, so anything
///     that lives only in that transcript is gone by the time the cursor acts.</para></summary>
public class ChainReviewAdvanceTests
{
  private static readonly DateTimeOffset T0 = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

  /// <summary>An in-memory chain store and a review store that keeps what it is given, so the
  ///     advance reads back exactly what the reviewer recorded.</summary>
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

      if (current.Version != expectedVersion)
      {
        return Task.FromResult(Result.Failure<Chain>(new DomainError("VersionConflict", "stale")));
      }

      Chain saved = chain with { Version = current.Version + 1 };
      _chains[saved.Id] = saved;
      return Task.FromResult(Result.Success(saved));
    }

    public Task<Result<Chain?>> FindBySessionAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Success(_chains.Values.FirstOrDefault(chain => chain.Steps
            .Any(step => string.Equals(step.SessionId, sessionId, StringComparison.Ordinal)))));
  }

  private sealed class FakeReviews : IChainReviewSource
  {
    private readonly List<(int ChainId, int Judged, string Session, ChainReviewRecord Review)> _rows = [];

    public Task<Result<ChainReviewRecord>> RecordAsync(int chainId, int judgedPosition,
        ChainReviewRecord review, string reviewerSessionId, DateTimeOffset now,
        CancellationToken ct = default)
    {
      _rows.Add((chainId, judgedPosition, reviewerSessionId, review));
      return Task.FromResult(Result.Success(review));
    }

    public Task<Result<ChainReviewRecord?>> FindForSessionAsync(int chainId, string reviewerSessionId,
        CancellationToken ct = default)
    {
      ChainReviewRecord? found = _rows
          .Where(row => row.ChainId == chainId
              && string.Equals(row.Session, reviewerSessionId, StringComparison.Ordinal))
          .Select(row => row.Review)
          .LastOrDefault();
      return Task.FromResult(Result.Success(found));
    }
  }

  private sealed class FakeHost : IChainAdvanceHost
  {
    public List<ChainStepAssignment> Started { get; } = [];

    public List<string> Closed { get; } = [];

    public Task CloseSessionAsync(string sessionId, CancellationToken ct = default)
    {
      Closed.Add(sessionId);
      return Task.CompletedTask;
    }

    public Task<Result<string>> StartSessionAsync(ChainStepAssignment assignment,
        string predecessorSessionId, CancellationToken ct = default)
    {
      Started.Add(assignment);
      return Task.FromResult(Result.Success("session-" + assignment.Position));
    }

    public Task<Result<string>> StartFinalSessionAsync(Chain chain, string predecessorSessionId,
        CancellationToken ct = default) => Task.FromResult(Result.Success("session-final"));

    public Task<Result<WorktreeProvision>> ProvisionWorktreeAsync(string name,
        CancellationToken ct = default)
        => Task.FromResult(Result.Success(
            new WorktreeProvision(name, "C:\\ws\\.worktrees\\" + name, name)));

    public Task<bool> WorktreeIsDirtyAsync(string path, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task RemoveWorktreeAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
  }

  private static T Must<T>(Result<T> result)
  {
    Assert.True(result.IsSuccess, result.Error?.Message ?? "expected success");
    return result.Value;
  }

  /// <summary>A chain whose step 1 is implementation and step 2 is a review of it, with both
  ///     steps' sessions already recorded so each can complete.
  ///     </summary>
  private static async Task<(ChainAdvanceService Service, ChainService Chains, FakeStore Store,
      FakeReviews Reviews, FakeHost Host, Chain Chain)> NewAsync()
  {
    FakeStore store = new();
    FakeReviews reviews = new();
    FakeHost host = new();
    ChainService chains = new(store);
    Chain chain = Must(await chains.CreateAsync(new ChainCreation("C:\\ws", 3, 1, "t",
        "session-root",
        [new ChainStepSeed(1, ChainStepRole.Implement), new ChainStepSeed(2, ChainStepRole.Review)]),
        T0, TestContext.Current.CancellationToken).ConfigureAwait(true));
    Chain withSessions = await SettleFirstAsync(store, chain).ConfigureAwait(true);
    return (new ChainAdvanceService(chains, host, new ChainReviewService(reviews, chains)), chains,
        store, reviews, host, withSessions);
  }

  /// <summary>Walks the chain to the state a review step is completed from: step 1 done, step 2
  ///     claimed by the reviewing session.</summary>
  private static async Task<Chain> SettleFirstAsync(FakeStore store, Chain chain)
  {
    Chain claimed = Must(await store.SaveAsync(chain.ClaimStep(1, "session-impl", T0),
        chain.Version, TestContext.Current.CancellationToken).ConfigureAwait(true)).ClaimStep(2,
        "session-review", T0);
    Chain settled = claimed.SettleStep(1, ChainStepStatus.Done, ChainActor.Session("session-impl"),
        "did the work", T0);
    return Must(await store.SaveAsync(settled, claimed.Version,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
  }

  [Fact]
  public async Task APass_AdvancesPastTheReviewStep_AndCompletesTheChain()
  {
    (ChainAdvanceService service, ChainService chains, FakeStore store, FakeReviews reviews, _,
        Chain chain) = await NewAsync();

    _ = Must(await new ChainReviewService(reviews, chains).RecordAsync("session-review",
        new ChainReviewRecord(ChainReviewVerdict.Pass, []), T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    ChainAdvance decision = Must(await service
        .OnCompletionAsync("session-review", planId: 3, T0, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));

    // THE PASS IS THE ORDINARY ADVANCE: the review step is the chain's last, so the chain finishes.
    Assert.Equal(ChainAdvanceKind.CompleteChain, decision.Kind);
    Chain stored = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));
    Assert.Equal(ChainStatus.Completed, stored.Status);
  }

  [Fact]
  public async Task AFail_SendsTheJudgedStepBack_AndStartsAFixSessionSeededWithTheFindings()
  {
    (ChainAdvanceService service, ChainService chains, FakeStore store, FakeReviews reviews,
        FakeHost host, Chain chain) = await NewAsync();

    _ = Must(await new ChainReviewService(reviews, chains).RecordAsync("session-review",
        new ChainReviewRecord(ChainReviewVerdict.Fail,
            [new ChainReviewFinding("the test asserts nothing", "step 1")]), T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    ChainAdvance decision = Must(await service
        .OnCompletionAsync("session-review", planId: 3, T0, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));

    // THE CURSOR MOVED BACK: a fix session starts for the step this review judged.
    Assert.Equal(ChainAdvanceKind.StartStep, decision.Kind);
    Assert.Equal(1, decision.Position);
    ChainStepAssignment fix = Assert.Single(host.Started);
    Assert.Equal(1, fix.Position);
    // THE FIX RUNS THE JUDGED STEP'S ROLE, read from the record rather than assumed.
    Assert.Equal(ChainStepRoleKind.Implement, fix.Role!.Kind);

    Chain stored = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));
    ChainStep judged = stored.StepAt(1)!;
    Assert.Equal(ChainStepStatus.Running, judged.Status);
    // THE FINDINGS ARE ON THE REOPENED STEP, verbatim and cited, so the fix session reads them.
    Assert.Contains("the test asserts nothing", judged.Outcome!, StringComparison.Ordinal);
    Assert.Contains("[cites: step 1]", judged.Outcome!, StringComparison.Ordinal);
    Assert.Equal(1, judged.ReviewAttempts);
  }

  [Fact]
  public async Task AReviewStepCompletedWithNoVerdict_IsRefused_AndTheStepStaysOpen()
  {
    (ChainAdvanceService service, _, FakeStore store, _, _, Chain chain) = await NewAsync();

    ChainAdvance decision = Must(await service
        .OnCompletionAsync("session-review", planId: 3, T0, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));

    // A REFUSAL, NOT A CRASH, and the session is left open so it can record its verdict.
    Assert.Equal(ChainAdvanceKind.Refused, decision.Kind);
    Chain stored = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));
    Assert.Equal(ChainStepStatus.Running, stored.StepAt(2)!.Status);
  }

  [Fact]
  public void TheReviewPolicy_AdvancesOnAPass_SendsBackOnAFail_AndParksAtTheCap()
  {
    ChainReviewDecision pass = ChainReviewPolicy.Decide(
        new ChainReviewRecord(ChainReviewVerdict.Pass, []), 1, 0);
    Assert.Equal(ChainReviewDecisionKind.Advance, pass.Kind);

    ChainReviewDecision fail = ChainReviewPolicy.Decide(
        new ChainReviewRecord(ChainReviewVerdict.Fail, [new ChainReviewFinding("no test", "step 1")]),
        1, 0);
    Assert.Equal(ChainReviewDecisionKind.SendBack, fail.Kind);
    Assert.Equal(1, fail.Target);
    // THE FINDINGS TRAVEL VERBATIM WITH THEIR CITATIONS: the fix session acts on what the
    // reviewer wrote, not on a summary of it.
    Assert.Contains("no test", fail.ReviewNotes!, StringComparison.Ordinal);
    Assert.Contains("[cites: step 1]", fail.ReviewNotes!, StringComparison.Ordinal);

    ChainReviewDecision capped = ChainReviewPolicy.Decide(
        new ChainReviewRecord(ChainReviewVerdict.Fail, [new ChainReviewFinding("again", "step 1")]),
        1, ChainReviewPolicy.MaxReviewAttempts);
    Assert.Equal(ChainReviewDecisionKind.ParkAtCap, capped.Kind);
  }

  [Fact]
  public void AMissingVerdict_IsRefused_SoTheChainCannotAdvanceOnNothing()
  {
    ChainReviewDecision none = ChainReviewPolicy.Decide(null, 1, 0);

    Assert.Equal(ChainReviewDecisionKind.Refused, none.Kind);
    Assert.Equal(ChainReviewCode.NoVerdict, none.Code);
  }

  [Fact]
  public void AFailWithNoFindings_IsRefused_BecauseNothingCouldBeFixed()
  {
    ChainReviewDecision empty = ChainReviewPolicy.Decide(
        new ChainReviewRecord(ChainReviewVerdict.Fail, []), 1, 0);

    Assert.Equal(ChainReviewDecisionKind.Refused, empty.Kind);
    Assert.Equal(ChainReviewCode.NoFindings, empty.Code);
  }

  [Fact]
  public void AReviewAtTheChainsFirstPosition_HasNothingToSendBack()
  {
    ChainReviewDecision orphan = ChainReviewPolicy.Decide(
        new ChainReviewRecord(ChainReviewVerdict.Fail, [new ChainReviewFinding("x", "step 0")]),
        null, 0);

    Assert.Equal(ChainReviewDecisionKind.Refused, orphan.Kind);
    Assert.Equal(ChainReviewCode.NoFixTarget, orphan.Code);
  }

  [Fact]
  public void ASentBackStep_ReopensWithTheFindings_AndSpendsAReviewRoundTripNotARetry()
  {
    ChainStep step = ChainStep.Pending(1, ChainStepRole.Implement)
        .Claim("session-impl", T0)
        .Settle(ChainStepStatus.Done, ChainActor.Session("session-impl"), "done", T0);

    ChainStep reopened = step.Reopen(ChainActor.Harness, "- no test [cites: step 1]");

    Assert.Equal(ChainStepStatus.Pending, reopened.Status);
    Assert.Equal(1, reopened.ReviewAttempts);
    // THE RETRY COUNTER IS UNTOUCHED: the session did not break, the work was wrong.
    Assert.Equal(step.Attempts, reopened.Attempts);
    Assert.Contains("no test", reopened.Outcome!, StringComparison.Ordinal);
    Assert.Null(reopened.SessionId);
  }

  [Fact]
  public void AStepThatIsNotDone_CannotBeReopened()
  {
    ChainStep pending = ChainStep.Pending(1, ChainStepRole.Implement);

    _ = Assert.Throws<ChainInputException>(
        () => pending.Reopen(ChainActor.Harness, "nothing to redo"));
  }

  [Fact]
  public void TheReviewBudgetIsSpentAtTheCap_AndComputedNotStored()
  {
    ChainStep step = ChainStep.Pending(1, ChainStepRole.Implement) with
    {
      ReviewAttempts = ChainReviewPolicy.MaxReviewAttempts,
    };

    Assert.True(step.ReviewBudgetSpent);
    Assert.False(step with { ReviewAttempts = 0 } is { ReviewBudgetSpent: true });
  }
}
