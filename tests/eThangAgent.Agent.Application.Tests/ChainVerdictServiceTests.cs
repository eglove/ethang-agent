using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>Recording a verdict MOVES THE CHAIN. This is the deadlock the batch pipeline hit: the
///     cursor read a verdict only when the review step settled, and the skill told a reviewer not
///     to settle its own step after a denial - so a reviewer that obeyed froze the chain with its
///     work done, and one that disobeyed advanced it with the findings unfixed.
///     <para>EVERY TEST HERE RECORDS A VERDICT AND ASSERTS THE CHAIN MOVED WITHOUT THE REVIEWER
///     COMPLETING ANYTHING. That is the whole contract: recording is the act.</para></summary>
public class ChainVerdictServiceTests
{
  private static readonly DateTimeOffset T0 = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

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
    private readonly List<(int ChainId, string Session, ChainReviewRecord Review)> _rows = [];

    public Task<Result<ChainReviewRecord>> RecordAsync(int chainId, int judgedPosition,
        ChainReviewRecord review, string reviewerSessionId, DateTimeOffset now,
        CancellationToken ct = default)
    {
      _rows.Add((chainId, reviewerSessionId, review));
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

  private sealed class FakeLaunch : IChainResumeLaunchPort
  {
    public List<ChainStepAssignment> Started { get; } = [];

    public Task<Result<string>> StartResumedSessionAsync(ChainStepAssignment assignment,
        CancellationToken ct = default)
    {
      Started.Add(assignment);
      return Task.FromResult(Result.Success("session-" + assignment.Position));
    }
  }

  private static T Must<T>(Result<T> result)
  {
    Assert.True(result.IsSuccess, result.Error?.Message ?? "expected success");
    return result.Value;
  }

  /// <summary>A chain whose step 1 is implementation and step 2 is a review of it, with both
  ///     sessions recorded and step 1 already done.</summary>
  private static async Task<(ChainVerdictService Service, ChainService Chains, FakeStore Store,
      FakeReviews Reviews, FakeLaunch Launch, Chain Chain)> NewAsync()
  {
    FakeStore store = new();
    FakeReviews reviews = new();
    FakeLaunch launch = new();
    ChainService chains = new(store);
    Chain chain = Must(await chains.CreateAsync(new ChainCreation("C:\\ws", 3, 1, "t",
        "session-root",
        [new ChainStepSeed(1, ChainStepRole.Implement), new ChainStepSeed(2, ChainStepRole.Review)]),
        T0, TestContext.Current.CancellationToken).ConfigureAwait(true));
    Chain withSessions = await SettleFirstAsync(store, chain).ConfigureAwait(true);
    ChainReviewService reviewService = new(reviews, chains);
    return (new ChainVerdictService(chains, reviewService, launch), chains, store, reviews,
        launch, withSessions);
  }

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
  public async Task APass_SettlesTheReviewStepAndCompletesTheChain_WithNoSecondAct()
  {
    // THE REVIEWER RECORDS AND STOPS. Nothing completes the review step on its behalf, and the
    // chain still finishes - which is the shape that used to require the reviewer to close it.
    (ChainVerdictService service, ChainService chains, FakeStore store, FakeReviews reviews, _,
        Chain chain) = await NewAsync();
    _ = Must(await new ChainReviewService(reviews, chains).RecordAsync("session-review",
        new ChainReviewRecord(ChainReviewVerdict.Pass, []), T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    ChainVerdictOutcome outcome = Must(await service.EnactAsync(chain.Id, "session-review", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Assert.Equal(ChainReviewDecisionKind.Advance, outcome.Kind);
    Chain stored = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));
    Assert.Equal(ChainStepStatus.Done, stored.StepAt(2)!.Status);
    Assert.Equal(ChainStatus.Completed, stored.Status);
  }

  [Fact]
  public async Task AFail_ReopensTheJudgedStep_AndStartsTheFix_WithNoSecondAct()
  {
    // THE DENIAL IS NOT A NOTE. It reopens the work it judged and starts the fix session, so a
    // reviewer that obeys 'do not mark your step done after a denial' still moves the chain.
    (ChainVerdictService service, ChainService chains, FakeStore store, FakeReviews reviews,
        FakeLaunch launch, Chain chain) = await NewAsync();
    _ = Must(await new ChainReviewService(reviews, chains).RecordAsync("session-review",
        new ChainReviewRecord(ChainReviewVerdict.Fail,
            [new ChainReviewFinding("the test asserts nothing", "step 1")]), T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    ChainVerdictOutcome outcome = Must(await service.EnactAsync(chain.Id, "session-review", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Assert.Equal(ChainReviewDecisionKind.SendBack, outcome.Kind);
    Assert.Equal(1, outcome.Target);
    Assert.Equal(1, Assert.Single(launch.Started).Position);
    Chain stored = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));
    Assert.Equal(ChainStepStatus.Done, stored.StepAt(2)!.Status);
    ChainStep judged = stored.StepAt(1)!;
    Assert.Equal(ChainStepStatus.Running, judged.Status);
    Assert.Contains("the test asserts nothing", judged.Outcome!, StringComparison.Ordinal);
    Assert.Equal(1, judged.ReviewAttempts);
  }

  [Fact]
  public async Task EnactingTheSameVerdictTwice_MovesTheChainOnce()
  {
    // A REVIEWER THAT ALSO MARKS ITS STEP DONE, or retries after a fault, must not start a second
    // successor for one piece of work.
    (ChainVerdictService service, ChainService chains, _, FakeReviews reviews,
        FakeLaunch launch, Chain chain) = await NewAsync();
    _ = Must(await new ChainReviewService(reviews, chains).RecordAsync("session-review",
        new ChainReviewRecord(ChainReviewVerdict.Fail,
            [new ChainReviewFinding("wrong", "step 1")]), T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    _ = Must(await service.EnactAsync(chain.Id, "session-review", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    ChainVerdictOutcome second = Must(await service.EnactAsync(chain.Id, "session-review", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Assert.Equal(1, Assert.Single(launch.Started).Position);
    Assert.Contains("already settled", second.Reason!, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ASessionThatHoldsNoReviewStep_IsRefusedByName()
  {
    (ChainVerdictService service, _, _, _, FakeLaunch launch, Chain chain) = await NewAsync();

    Result<ChainVerdictOutcome> outcome = await service.EnactAsync(chain.Id, "session-impl", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(outcome.IsSuccess);
    Assert.Equal(ChainReviewCode.NotAReviewStep, outcome.Error.Code);
    Assert.Empty(launch.Started);
  }
}
