using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Issue #218 D4/D5: the gate's two answers. A MISSING model is a typed refusal that
///     starts nothing; a MATCH parks the chain with a reason naming both models and the step.
///     The gate writes through <see cref="ChainService"/>, so these drive a fake store - the
///     rule is pure and this is the only place the side effect lives.</summary>
public class ChainReviewerGateTests
{
  private static readonly DateTimeOffset T0 = new(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);

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
        => Task.FromResult(Result.Success(new ChainCatalog([.. _chains.Values], [])));

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

  /// <summary>Unwraps a success or fails the test with the domain error's own words.</summary>
  private static T Must<T>(Result<T> result)
  {
    Assert.True(result.IsSuccess, result.Error?.Message ?? "expected success");
    return result.Value;
  }

  private static async Task<(ChainReviewerGate Gate, ChainService Chains, Chain Chain)> NewAsync(
      params ChainStepSeed[] seeds)
  {
    FakeStore store = new();
    ChainService chains = new(store);
    Chain chain = Must(await chains.CreateAsync(
        new ChainCreation("C:\\ws", 3, 1, "t", "session-root", seeds), T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    return (new ChainReviewerGate(chains), chains, chain);
  }

  [Fact]
  public async Task CheckAsync_AnImplementerStep_IsCleared()
  {
    (ChainReviewerGate gate, _, Chain chain) = await NewAsync(ChainStepSeed.Implementing(1));

    ChainReviewerCheck check = Must(await gate.CheckAsync(chain.Id, 1, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Assert.False(check.Parked);
    Assert.Null(check.Reason);
  }

  [Fact]
  public async Task CheckAsync_AReviewStepWithNoModel_FailsWithATypedError_AndStartsNothing()
  {
    (ChainReviewerGate gate, ChainService chains, Chain chain) = await NewAsync(
        ChainStepSeed.Implementing(1), new ChainStepSeed(2, ChainStepRole.Review()));

    Result<ChainReviewerCheck> check = await gate.CheckAsync(chain.Id, 2, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(check.IsSuccess);
    Assert.Equal(ReviewerSeparationCode.ModelMissing, check.Error.Code);
    // NOTHING WAS PARKED AND NOTHING WAS CLAIMED: a missing model is a declaration defect, so
    // the record is untouched and the step is still available to a corrected launch.
    Chain after = Must(await chains.GetAsync(chain.Id, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));
    Assert.Equal(ChainStatus.Running, after.Status);
    Assert.Equal(ChainStepStatus.Pending, after.StepAt(2)!.Status);
  }

  [Fact]
  public async Task CheckAsync_B4_B5_AMatchingModel_ParksTheChain_NamingBothModelsAndTheStep()
  {
    (ChainReviewerGate gate, ChainService chains, Chain chain) = await NewAsync(
        ChainStepSeed.Implementing(1),
        new ChainStepSeed(2, ChainStepRole.Review("vendor/implementer")));
    _ = Must(await chains.RecordServedModelAsync(chain.Id, 1, "vendor/implementer", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    ChainReviewerCheck check = Must(await gate.CheckAsync(chain.Id, 2, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Assert.True(check.Parked);
    Assert.NotNull(check.Reason);
    Assert.Contains("step 2", check.Reason, StringComparison.Ordinal);
    Assert.Contains("vendor/implementer", check.Reason, StringComparison.Ordinal);
    Chain parked = Must(await chains.GetAsync(chain.Id, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));
    Assert.Equal(ChainStatus.Parked, parked.Status);
    Assert.Equal(2, parked.ParkedAtPosition);
    Assert.Equal(check.Reason, parked.ParkReason);
  }

  [Fact]
  public async Task CheckAsync_ADifferentModel_IsCleared_AndNothingIsParked()
  {
    (ChainReviewerGate gate, ChainService chains, Chain chain) = await NewAsync(
        ChainStepSeed.Implementing(1),
        new ChainStepSeed(2, ChainStepRole.Review("vendor/reviewer")));
    _ = Must(await chains.RecordServedModelAsync(chain.Id, 1, "vendor/implementer", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    ChainReviewerCheck check = Must(await gate.CheckAsync(chain.Id, 2, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Assert.False(check.Parked);
    Chain after = Must(await chains.GetAsync(chain.Id, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));
    Assert.Equal(ChainStatus.Running, after.Status);
  }

  [Fact]
  public async Task CheckAsync_AnUnknownPosition_IsCleared()
  {
    (ChainReviewerGate gate, _, Chain chain) = await NewAsync(ChainStepSeed.Implementing(1));

    ChainReviewerCheck check = Must(await gate.CheckAsync(chain.Id, 9, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    Assert.False(check.Parked);
  }
}
