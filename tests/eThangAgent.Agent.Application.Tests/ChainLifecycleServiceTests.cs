using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>Issue #303: a step failure against a FAKE store and a FAKE host - resume the same
///     session while the budget allows, park the chain when it does not, and spend no attempt
///     on a session that is unreachable. No database, no tab, no host process.</summary>
public class ChainLifecycleServiceTests
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

  private sealed class FakeHost : IChainLifecycleHost
  {
    public List<(string SessionId, string Message)> Resumed { get; } = [];

    public bool Reachable { get; set; } = true;

    public Task<bool> TryResumeAsync(string sessionId, string message, CancellationToken ct = default)
    {
      Resumed.Add((sessionId, message));
      return Task.FromResult(Reachable);
    }
  }

  private static T Must<T>(Result<T> result)
  {
    Assert.True(result.IsSuccess, result.Error?.Message ?? "expected success");
    return result.Value;
  }

  /// <summary>A chain with <paramref name="positions"/> steps whose step 1 is claimed by
  ///     session-a, claimed <paramref name="attempts"/> times in total.</summary>
  private static async Task<(ChainLifecycleService Service, FakeStore Store, FakeHost Host, Chain Chain)>
      NewAsync(int attempts = 1, params int[] positions)
  {
    FakeStore store = new();
    FakeHost host = new();
    ChainService chains = new(store);
    Chain chain = Must(await chains.CreateAsync(ChainCreation.Implementing("C:\\ws", 3, 1, "t",
        "session-root", positions.Length == 0 ? [1] : positions), T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    DateTimeOffset at = T0;
    for (int i = 0; i < attempts; i++)
    {
      chain = Must(await chains.ClaimStepAsync(chain.Id, 1, "session-a", at,
          TestContext.Current.CancellationToken).ConfigureAwait(true));
      at += ChainClaimPolicy.ClaimTtl + TimeSpan.FromMinutes(1);
    }

    return (new ChainLifecycleService(chains, host), store, host, chain);
  }

  [Fact]
  public async Task OnStepFailed_UnderTheCap_ResumesTheSameSession()
  {
    (ChainLifecycleService service, FakeStore store, FakeHost host, Chain chain) = await NewAsync();

    ChainFailureOutcome outcome = Must(await service.OnStepFailedAsync("session-a", "provider blip", T0,
        TestContext.Current.CancellationToken));

    Assert.True(outcome.Retried);
    Assert.Equal(1, outcome.Position);
    (string sessionId, string _) = Assert.Single(host.Resumed);
    Assert.Equal("session-a", sessionId);
    Chain after = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken));
    Assert.Equal(ChainStepStatus.Running, after.Steps[0].Status);
    Assert.Equal("session-a", after.Steps[0].SessionId);
  }

  [Fact]
  public async Task OnStepFailed_TheResumeCarriesTheFailureNote_NotTheWatchdogNudge()
  {
    (ChainLifecycleService service, _, FakeHost host, Chain chain) = await NewAsync();

    _ = Must(await service.OnStepFailedAsync("session-a", "provider returned 503", T0,
        TestContext.Current.CancellationToken));

    string note = host.Resumed[0].Message;
    Assert.Contains("provider returned 503", note, StringComparison.Ordinal);
    Assert.NotEqual(RootSupervisionPolicy.Nudge(chain.Id, 1), note);
  }

  [Fact]
  public async Task OnStepFailed_AtTheCap_ParksTheChainNamingTheStep()
  {
    (ChainLifecycleService service, FakeStore store, FakeHost host, Chain chain) =
        await NewAsync(attempts: ChainRetryPolicy.MaxAttempts);

    ChainFailureOutcome outcome = Must(await service.OnStepFailedAsync("session-a", "keeps failing", T0,
        TestContext.Current.CancellationToken));

    Assert.True(outcome.Parked);
    Assert.Empty(host.Resumed); // no resume at the cap
    Chain after = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken));
    Assert.Equal(ChainStatus.Parked, after.Status);
    Assert.Equal(1, after.ParkedAtPosition);
    Assert.Contains("step 1", after.ParkReason!, StringComparison.Ordinal);
    Assert.Contains("3-attempt cap", after.ParkReason!, StringComparison.Ordinal);
    Assert.Contains("keeps failing", after.ParkReason!, StringComparison.Ordinal);
  }

  [Fact]
  public async Task OnStepFailed_WhenTheSessionIsUnreachable_ParksWithoutSpendingTheBudget()
  {
    (ChainLifecycleService service, FakeStore store, FakeHost host, Chain chain) = await NewAsync();
    host.Reachable = false;

    ChainFailureOutcome outcome = Must(await service.OnStepFailedAsync("session-a", "boom", T0,
        TestContext.Current.CancellationToken));

    Assert.True(outcome.Parked);
    Assert.Contains("unreachable", outcome.Reason!, StringComparison.Ordinal);
    Chain after = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken));
    Assert.Equal(ChainStatus.Parked, after.Status);
  }

  [Fact]
  public async Task OnStepFailed_ForASessionInNoChain_IsNotAnError()
  {
    (ChainLifecycleService service, _, FakeHost host, _) = await NewAsync();

    ChainFailureOutcome outcome = Must(await service.OnStepFailedAsync("session-other", null, T0,
        TestContext.Current.CancellationToken));

    Assert.Null(outcome.Position);
    Assert.Empty(host.Resumed);
    Assert.Contains("not assigned to any chain step", outcome.Reason!, StringComparison.Ordinal);
  }

  [Fact]
  public async Task OnStepFailed_RecordsTheFailureAsTheStepOutcome()
  {
    (ChainLifecycleService service, FakeStore store, _, Chain chain) = await NewAsync();

    _ = Must(await service.OnStepFailedAsync("session-a", "provider blip", T0,
        TestContext.Current.CancellationToken));

    Chain after = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken));
    Assert.Equal("provider blip", after.Steps[0].Outcome);
  }

  [Fact]
  public async Task OnStepFailed_KeepsTheAttemptCount_SoTheBoundSurvivesARetry()
  {
    (ChainLifecycleService service, FakeStore store, _, Chain chain) = await NewAsync();

    _ = Must(await service.OnStepFailedAsync("session-a", "blip", T0,
        TestContext.Current.CancellationToken));
    Chain after = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken));

    // A retry is not a new attempt: the count stays where the claim left it.
    Assert.Equal(1, after.Steps[0].Attempts);
  }
}
