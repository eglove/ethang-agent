using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>Issue #303's exit: resuming a parked chain against a FAKE store and a FAKE launch -
///     the chain un-parks, the step that stopped it is re-armed, and a session is started for
///     what is now available. No database, no tab, no host process.</summary>
public class ChainResumeServiceTests
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

  private sealed class FakeLaunch : IChainResumeLaunchPort
  {
    public List<ChainStepAssignment> Started { get; } = [];

    public Result<string>? StartResult { get; set; }


    public Task<Result<string>> StartResumedSessionAsync(ChainStepAssignment assignment,
        CancellationToken ct = default)
    {
      Started.Add(assignment);
      return Task.FromResult(StartResult ?? Result.Success("session-resumed"));
    }
  }

  private static T Must<T>(Result<T> result)
  {
    Assert.True(result.IsSuccess, result.Error?.Message ?? "expected success");
    return result.Value;
  }

  /// <summary>A chain parked on a spent attempt budget: step 1 Failed, the chain Parked at it.
  ///     This is the state that could not be escaped before the resume existed.</summary>
  private static async Task<(ChainResumeService Service, FakeStore Store, FakeLaunch Launch, Chain Chain)>
      ParkedAsync()
  {
    FakeStore store = new();
    FakeLaunch launch = new();
    ChainService chains = new(store);
    Chain chain = Must(await chains.CreateAsync(ChainCreation.Implementing("C:\\ws", 3, 1, "t",
        "session-root", [1, 2]), T0, TestContext.Current.CancellationToken).ConfigureAwait(true));
    _ = Must(await chains.ClaimStepAsync(chain.Id, 1, "session-a", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    _ = Must(await chains.SettleStepAsync(chain.Id, 1, ChainStepStatus.Failed,
        ChainActor.Session("session-a"), "it broke", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    _ = Must(await chains.ParkAsync(chain.Id, "step 1 failed 3 times", 1, ChainActor.Harness, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    return (new ChainResumeService(chains, launch), store, launch, chain);
  }

  [Fact]
  public async Task ResumeAsync_UnparksTheChain_AndStartsTheRearmedStep()
  {
    (ChainResumeService service, FakeStore store, FakeLaunch launch, Chain chain) =
        await ParkedAsync();

    ChainResumeOutcome outcome = Must(await service.ResumeAsync(chain.Id, ChainActor.User, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Assert.Equal(1, outcome.RearmedPosition);
    Assert.Equal(1, outcome.StartedPosition);
    Assert.Equal("session-resumed", outcome.SessionId);
    _ = Assert.Single(launch.Started);

    // THE WHOLE POINT: the chain is Running AND the step that stopped it is claimable again.
    Chain stored = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken));
    Assert.Equal(ChainStatus.Running, stored.Status);
    Assert.Null(stored.ParkReason);
    ChainStep step = stored.StepAt(1)!;
    Assert.Equal(ChainStepStatus.Running, step.Status);
    Assert.Equal("session-resumed", step.SessionId);
  }

  [Fact]
  public async Task ResumeAsync_OnARunningChain_FailsChainNotParked_AndStartsNothing()
  {
    FakeStore store = new();
    FakeLaunch launch = new();
    ChainService chains = new(store);
    Chain chain = Must(await chains.CreateAsync(ChainCreation.Implementing("C:\\ws", 3, 1, "t",
        "session-root", [1]), T0, TestContext.Current.CancellationToken).ConfigureAwait(true));
    ChainResumeService service = new(chains, launch);

    Result<ChainResumeOutcome> refused = await service.ResumeAsync(chain.Id, ChainActor.User, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(refused.IsSuccess);
    Assert.Equal(ChainResumeCode.NotParked, refused.Error.Code);
    Assert.Empty(launch.Started);
  }

  [Fact]
  public async Task ResumeAsync_WhenTheLaunchFails_LeavesTheStepAvailable_AndReleasesTheClaim()
  {
    (ChainResumeService service, FakeStore store, FakeLaunch launch, Chain chain) =
        await ParkedAsync();
    launch.StartResult = Result.Failure<string>(new DomainError("TabClosed", "no tab"));

    Result<ChainResumeOutcome> refused = await service.ResumeAsync(chain.Id, ChainActor.User, T0,
        TestContext.Current.CancellationToken);

    Assert.False(refused.IsSuccess);
    Assert.Equal("TabClosed", refused.Error.Code);

    // The chain stays resumed and the step stays AVAILABLE, so calling again retries the
    // launch rather than leaving a claim held by a session that never existed.
    Chain stored = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken));
    Assert.Equal(ChainStatus.Running, stored.Status);
    ChainStep step = stored.StepAt(1)!;
    Assert.Equal(ChainStepStatus.Pending, step.Status);
    Assert.Null(step.SessionId);
  }

  [Fact]
  public async Task ResumeAsync_WithNoParkedPosition_StartsTheStepThatIsAlreadyAvailable()
  {
    // A park that named no step (a chain that parked because nothing was available) re-arms
    // nothing, but the step it offers is still started: the resume's job is to make the chain
    // move, not merely to change its status.
    FakeStore store = new();
    FakeLaunch launch = new();
    ChainService chains = new(store);
    Chain chain = Must(await chains.CreateAsync(ChainCreation.Implementing("C:\\ws", 3, 1, "t",
        "session-root", [1, 2]), T0, TestContext.Current.CancellationToken).ConfigureAwait(true));
    _ = Must(await chains.ParkAsync(chain.Id, "waiting", null, ChainActor.Harness, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    ChainResumeService service = new(chains, launch);

    ChainResumeOutcome outcome = Must(await service.ResumeAsync(chain.Id, ChainActor.User, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Assert.Null(outcome.RearmedPosition);
    Assert.Equal(1, outcome.StartedPosition);
    _ = Assert.Single(launch.Started);
    Chain stored = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken));
    Assert.Equal(ChainStatus.Running, stored.Status);
  }

  [Fact]
  public async Task ResumeAsync_WithNoStepAvailable_ReportsThatNothingStarted()
  {
    // Every step is already Done, so the resume un-parks the chain and starts nothing: the
    // report says so rather than leaving a reader to infer it from a record they cannot see.
    FakeStore store = new();
    FakeLaunch launch = new();
    ChainService chains = new(store);
    Chain chain = Must(await chains.CreateAsync(ChainCreation.Implementing("C:\\ws", 3, 1, "t",
        "session-root", [1]), T0, TestContext.Current.CancellationToken).ConfigureAwait(true));
    _ = Must(await chains.ClaimStepAsync(chain.Id, 1, "session-a", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    _ = Must(await chains.SettleStepAsync(chain.Id, 1, ChainStepStatus.Done,
        ChainActor.Session("session-a"), null, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    _ = Must(await chains.ParkAsync(chain.Id, "waiting", null, ChainActor.Harness, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    ChainResumeService service = new(chains, launch);

    ChainResumeOutcome outcome = Must(await service.ResumeAsync(chain.Id, ChainActor.User, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Assert.Null(outcome.RearmedPosition);
    Assert.Null(outcome.StartedPosition);
    Assert.Empty(launch.Started);
  }
}
