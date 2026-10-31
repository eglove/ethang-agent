using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>Issue #304: an act on the universal floor PARKED the chain.
///     <para>THE REFUSAL AND THE PARK ARE TWO ACTS IN TWO DOMAINS. The refusal happens inside
///     an exec program (the Tool Domain, which knows nothing about chains); the park happens
///     here, reading the ambient ledger the refusal recorded into. This covers the second
///     half - the first is the Roslyn ACL's own test.</para></summary>
public class ChainFloorServiceTests
{
  private static readonly DateTimeOffset T0 = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

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

  private static T Must<T>(Result<T> result)
  {
    Assert.True(result.IsSuccess, result.Error?.Message ?? "expected success");
    return result.Value;
  }

  private static async Task<(ChainFloorService Service, FakeStore Store, Chain Chain)> RunningAsync(
      ChainFloorLedger ledger)
  {
    FakeStore store = new();
    ChainService chains = new(store);
    Chain chain = Must(await chains.CreateAsync(
        ChainCreation.Implementing("C:\\ws", 3, 1, "t", "session-root", [1, 2]), T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    Chain claimed = Must(await chains.ClaimStepAsync(chain.Id, 1, "session-a", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    Assert.Equal(ChainStepStatus.Running, claimed.Steps[0].Status);
    return (new ChainFloorService(chains, ledger), store, chain);
  }

  // THE ACCEPTANCE CRITERION: hitting the floor parks the chain.
  [Fact]
  public async Task OnTurnEnd_WithATrippedFloor_ParksTheChain()
  {
    ChainFloorLedger ledger = new();
    (ChainFloorService service, FakeStore store, Chain chain) = await RunningAsync(ledger);
    _ = ledger.Trip(ChainFloorRules.Rules[0], "git push --force");

    ChainFloorOutcome outcome = Must(await service.OnTurnEndAsync("session-a", T0,
        TestContext.Current.CancellationToken));

    Assert.True(outcome.Parked);
    Assert.Equal(chain.Id, outcome.ChainId);
    Chain stored = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken));
    Assert.Equal(ChainStatus.Parked, stored.Status);
  }

  // A PARK IS WAITING FOR A HUMAN, so the reason must name the act that stopped it.
  [Fact]
  public async Task OnTurnEnd_WithATrippedFloor_NamesTheActInTheParkReason()
  {
    ChainFloorLedger ledger = new();
    (ChainFloorService service, FakeStore store, Chain chain) = await RunningAsync(ledger);
    _ = ledger.Trip(ChainFloorRules.Rules[0], "git push --force");

    _ = await service.OnTurnEndAsync("session-a", T0, TestContext.Current.CancellationToken);

    Chain stored = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken));
    Assert.Contains("chain floor", stored.ParkReason, StringComparison.Ordinal);
    Assert.Contains("force-push", stored.ParkReason, StringComparison.Ordinal);
  }

  // ONCE PER TURN: three trips park once and name the FIRST act.
  [Fact]
  public async Task OnTurnEnd_WithSeveralTrips_NamesTheFirstAct()
  {
    ChainFloorLedger ledger = new();
    (ChainFloorService service, FakeStore store, Chain chain) = await RunningAsync(ledger);
    _ = ledger.Trip(ChainFloorRules.Rules[0], "git push --force");
    _ = ledger.Trip(ChainFloorRules.Rules[2], "npm publish");

    ChainFloorOutcome outcome = Must(await service.OnTurnEndAsync("session-a", T0,
        TestContext.Current.CancellationToken));

    Assert.True(outcome.Parked);
    Assert.Contains("refused as force-push", outcome.Reason, StringComparison.Ordinal);
    Assert.DoesNotContain("refused as publish", outcome.Reason, StringComparison.Ordinal);
    Chain stored = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken));
    Assert.Equal(ChainStatus.Parked, stored.Status);
  }

  // THE LEDGER IS CLEARED, so one turn's refusal never parks the next turn.
  [Fact]
  public async Task OnTurnEnd_ClearsTheLedger()
  {
    ChainFloorLedger ledger = new();
    (ChainFloorService service, _, _) = await RunningAsync(ledger);
    _ = ledger.Trip(ChainFloorRules.Rules[0], "git push --force");

    _ = await service.OnTurnEndAsync("session-a", T0, TestContext.Current.CancellationToken);

    Assert.False(ledger.Tripped);
  }

  // A TURN THAT TRIPPED NOTHING PARKS NOTHING: the ordinary case.
  [Fact]
  public async Task OnTurnEnd_WithNoTrip_ParksNothing()
  {
    ChainFloorLedger ledger = new();
    (ChainFloorService service, FakeStore store, Chain chain) = await RunningAsync(ledger);

    ChainFloorOutcome outcome = Must(await service.OnTurnEndAsync("session-a", T0,
        TestContext.Current.CancellationToken));

    Assert.False(outcome.Parked);
    Chain stored = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken));
    Assert.Equal(ChainStatus.Running, stored.Status);
  }

  // AN ORDINARY SESSION PARKS NOTHING: the refusal the model already read is the whole
  // consequence, exactly as the advance and the lifecycle answer a session in no chain.
  [Fact]
  public async Task OnTurnEnd_ForASessionWithNoChainStep_ParksNothing()
  {
    ChainFloorLedger ledger = new();
    (ChainFloorService service, FakeStore store, Chain chain) = await RunningAsync(ledger);
    _ = ledger.Trip(ChainFloorRules.Rules[0], "git push --force");

    ChainFloorOutcome outcome = Must(await service.OnTurnEndAsync("session-stranger", T0,
        TestContext.Current.CancellationToken));

    Assert.False(outcome.Parked);
    Assert.Contains("not assigned", outcome.Reason, StringComparison.Ordinal);
    Chain stored = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken));
    Assert.Equal(ChainStatus.Running, stored.Status);
  }
}
