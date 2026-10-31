using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>Issue #304 at the CHAIN level: a chain claiming Completed is a completion claim, so
///     it asks the SAME gate a step does (D1), and a blocking verdict PARKS rather than
///     completes. The gate is faked here - the real evaluator is the Tool Domain's.
///     <para>THE UNATTENDED CLAIM IS THE POINT: no human is watching when the last step
///     finishes, which is exactly why the harness has to ask.</para></summary>
public class ChainCompletionGateTests
{
  private static readonly DateTimeOffset T0 = new(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);

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

  private sealed class FakeHost : IChainAdvanceHost
  {
    public List<string> Closed { get; } = [];

    public Task CloseSessionAsync(string sessionId, CancellationToken ct = default)
    {
      Closed.Add(sessionId);
      return Task.CompletedTask;
    }

    public Task<Result<string>> StartSessionAsync(ChainStepAssignment assignment,
        string predecessorSessionId, CancellationToken ct = default) =>
        Task.FromResult(Result.Success("session-next"));
  }

  private sealed class FixedGate(bool allowed, string message) : IChainCompletionGate
  {
    public int Calls { get; private set; }

    public Task<(bool Allowed, string Message)> EvaluateAsync(CancellationToken ct = default)
    {
      Calls++;
      return Task.FromResult((allowed, message));
    }
  }

  private static T Must<T>(Result<T> result)
  {
    Assert.True(result.IsSuccess, result.Error?.Message ?? "expected success");
    return result.Value;
  }

  private static async Task<(ChainAdvanceService Service, FakeStore Store, FakeHost Host, Chain Chain)>
      NewAsync(IChainCompletionGate? gate)
  {
    FakeStore store = new();
    FakeHost host = new();
    ChainService chains = new(store);
    Chain chain = Must(await chains.CreateAsync(
        ChainCreation.Implementing("C:\\ws", 3, 1, "t", "session-root", [1]), T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    _ = await store.SaveAsync(chain.ClaimStep(1, "session-a", T0), chain.Version,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    return (new ChainAdvanceService(chains, host, completionGate: gate), store, host, chain);
  }

  // THE ACCEPTANCE CRITERION: a chain cannot reach Completed without a fresh passing gate.
  [Fact]
  public async Task Complete_WithStaleGate_ParksTheChainInsteadOfCompleting()
  {
    (ChainAdvanceService service, FakeStore store, _, Chain chain) = await NewAsync(
        new FixedGate(false, "[completion gate] the gate is stale; run state.checkgoal"));

    ChainAdvance decision = Must(await service.OnCompletionAsync("session-a", planId: 3, T0,
        TestContext.Current.CancellationToken));

    Assert.Equal(ChainAdvanceKind.Blocked, decision.Kind);
    Assert.Equal(ChainAdvanceCode.CompletionGateNotFresh, decision.Code);
    Assert.Contains("state.checkgoal", decision.Reason, StringComparison.Ordinal);
    Chain stored = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken));
    Assert.Equal(ChainStatus.Parked, stored.Status);
  }

  // A PARKED CHAIN IS WAITING FOR A HUMAN, so the reason must say WHY it stopped.
  [Fact]
  public async Task Complete_WithStaleGate_RecordsTheGateSentenceAsTheParkReason()
  {
    (ChainAdvanceService service, FakeStore store, _, Chain chain) = await NewAsync(
        new FixedGate(false, "[completion gate] the gate is stale; run state.checkgoal"));

    _ = await service.OnCompletionAsync("session-a", planId: 3, T0,
        TestContext.Current.CancellationToken);

    Chain stored = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken));
    Assert.Contains("completion gate", stored.ParkReason, StringComparison.Ordinal);
    Assert.Equal(1, stored.ParkedAtPosition);
  }

  // THE SESSION IS NOT CLOSED: the person un-parking the chain will want to read the
  // transcript that got it there.
  [Fact]
  public async Task Complete_WithStaleGate_LeavesTheSessionOpen()
  {
    (ChainAdvanceService service, _, FakeHost host, _) = await NewAsync(
        new FixedGate(false, "stale"));

    _ = await service.OnCompletionAsync("session-a", planId: 3, T0,
        TestContext.Current.CancellationToken);

    Assert.Empty(host.Closed);
  }

  [Fact]
  public async Task Complete_WithFreshGate_CompletesTheChain()
  {
    (ChainAdvanceService service, FakeStore store, FakeHost host, Chain chain) = await NewAsync(
        new FixedGate(true, "fresh"));

    ChainAdvance decision = Must(await service.OnCompletionAsync("session-a", planId: 3, T0,
        TestContext.Current.CancellationToken));

    Assert.Equal(ChainAdvanceKind.CompleteChain, decision.Kind);
    Assert.Contains("session-a", host.Closed);
    Chain stored = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken));
    Assert.Equal(ChainStatus.Completed, stored.Status);
  }

  // AN UNSET GATE NEVER BLOCKS (D2), and a container with no gate wired behaves as before.
  [Fact]
  public async Task Complete_WithNoGateWired_CompletesTheChain()
  {
    (ChainAdvanceService service, FakeStore store, _, Chain chain) = await NewAsync(gate: null);

    ChainAdvance decision = Must(await service.OnCompletionAsync("session-a", planId: 3, T0,
        TestContext.Current.CancellationToken));

    Assert.Equal(ChainAdvanceKind.CompleteChain, decision.Kind);
    Chain stored = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken));
    Assert.Equal(ChainStatus.Completed, stored.Status);
  }

  // THE GATE IS ASKED ONCE PER COMPLETION CLAIM, never on a step that merely advances: a
  // mid-chain advance has claimed nothing about the workspace as a whole.
  [Fact]
  public async Task Advance_DoesNotConsultTheChainGate()
  {
    FakeStore store = new();
    FakeHost host = new();
    ChainService chains = new(store);
    FixedGate gate = new(false, "stale");
    Chain chain = Must(await chains.CreateAsync(
        ChainCreation.Implementing("C:\\ws", 3, 1, "t", "session-root", [1, 2]), T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    _ = await store.SaveAsync(chain.ClaimStep(1, "session-a", T0), chain.Version,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    ChainAdvance decision = Must(await new ChainAdvanceService(chains, host, completionGate: gate)
        .OnCompletionAsync("session-a", planId: 3, T0,
            TestContext.Current.CancellationToken).ConfigureAwait(true));

    Assert.Equal(ChainAdvanceKind.StartStep, decision.Kind);
    Assert.Equal(0, gate.Calls);
  }
}
