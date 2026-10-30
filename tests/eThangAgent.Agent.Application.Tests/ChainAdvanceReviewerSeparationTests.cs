using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>Issue #218: the separation rule as the ADVANCE enforces it - a review step is gated
///     before it is claimed, so a refusal starts no session and spends no attempt, and the
///     implementer's served model is stamped on the record so the comparison survives a restart.
///     Fake store, fake host, fake agent store: no database, no tab, no host process.</summary>
public class ChainAdvanceReviewerSeparationTests
{
  private static readonly DateTimeOffset T0 = new(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);

  /// <summary>A GUID-shaped session id, because the implementer's served model is read from the
  ///     AGENT ROW and only a real session id names one (a provisional 'pending-...' claim names
  ///     no session at all, so there is nothing to read).</summary>
  private const string Implementer = "6f1a3c1e-0d1f-4a3b-9f4a-2b7c8d9e0f11";

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
    public List<ChainStepAssignment> Started { get; } = [];

    public Task CloseSessionAsync(string sessionId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<Result<string>> StartSessionAsync(ChainStepAssignment assignment,
        string predecessorSessionId, CancellationToken ct = default)
    {
      Started.Add(assignment);
      return Task.FromResult(Result.Success("session-next"));
    }
  }

  /// <summary>An agent store that answers one served model per session id.</summary>
  private sealed class FakeAgents : IAgentStore
  {
    public Dictionary<string, string> Served { get; } = new(StringComparer.Ordinal);

    public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success(record.Id.ToString()));

    public Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success(record.Id.ToString()));

    public Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Served.TryGetValue(id.ToString(), out string? model)
            ? Result.Success(AgentRecord.Spawned(id, null, 0, model, null, "s", T0))
            : Result.Failure<AgentRecord>(new DomainError("NotFound", "no such agent")));

    public Task<Result<string>> AppendMessageAsync(AgentId id,
        Message message, CancellationToken ct = default)
        => Task.FromResult(Result.Success("ok"));

    public Task<Result<string>> ReplaceTranscriptAsync(AgentId id,
        IReadOnlyList<Message> messages, CancellationToken ct = default)
        => Task.FromResult(Result.Success("ok"));

    public Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(
        AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<Message>>([]));

    public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId,
        CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([]));

    public Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([]));
  }

  /// <summary>Unwraps a success or fails the test with the domain error's own words - the
  ///     existing suite's helper, so a refused setup names why rather than throwing an
  ///     unexplained null dereference.</summary>
  private static T Must<T>(Result<T> result)
  {
    Assert.True(result.IsSuccess, result.Error?.Message ?? "expected success");
    return result.Value;
  }

  private static async Task<(ChainAdvanceService Service, ChainService Chains, FakeHost Host,
      FakeAgents Agents, Chain Chain)> NewAsync(params ChainStepSeed[] seeds)
  {
    FakeStore store = new();
    FakeHost host = new();
    FakeAgents agents = new();
    ChainService chains = new(store);
    Chain chain = Must(await chains.CreateAsync(
        new ChainCreation("C:\\ws", 3, 1, "t", "session-root", seeds), T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    ChainAdvanceService service = new(chains, host, new ChainReviewerGate(chains), agents);
    return (service, chains, host, agents, chain);
  }

  private static async Task<Chain> ClaimAsync(ChainService chains, Chain chain, int position,
      string sessionId)
    => Must(await chains.ClaimStepAsync(chain.Id, position, sessionId, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

  [Fact]
  public async Task OnCompletionAsync_B2_B3_RecordsTheImplementersServedModel_OnItsStep()
  {
    (ChainAdvanceService service, ChainService chains, FakeHost _, FakeAgents agents, Chain chain) =
        await NewAsync(ChainStepSeed.Implementing(1),
            new ChainStepSeed(2, ChainStepRole.Review("vendor/reviewer")));
    Chain claimed = await ClaimAsync(chains, chain, 1, Implementer);
    agents.Served[Implementer] = "vendor/implementer";

    ChainAdvance decision = Must(await service.OnCompletionAsync(Implementer, planId: 3, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Chain after = Must(await chains.GetAsync(claimed.Id, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));
    Assert.Equal("vendor/implementer", after.StepAt(1)!.ServedModel);
    // B2's comparison ran against that recorded model, and the reviewer is a DIFFERENT one, so
    // the review step was started rather than refused.
    Assert.Equal(ChainAdvanceKind.StartStep, decision.Kind);
    Assert.Equal(2, decision.Position);
  }

  [Fact]
  public async Task OnCompletionAsync_B4_AMatchingReviewer_StartsNoSession_AndSpendsNoAttempt()
  {
    (ChainAdvanceService service, ChainService chains, FakeHost host, FakeAgents agents, Chain chain) =
        await NewAsync(ChainStepSeed.Implementing(1),
            new ChainStepSeed(2, ChainStepRole.Review("vendor/implementer")));
    _ = await ClaimAsync(chains, chain, 1, Implementer);
    agents.Served[Implementer] = "vendor/implementer";

    ChainAdvance decision = Must(await service.OnCompletionAsync(Implementer, planId: 3, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    // NO SESSION STARTED - the rule refused before the claim, so nothing was launched.
    Assert.Empty(host.Started);
    Assert.Equal(ChainAdvanceKind.Blocked, decision.Kind);
    Assert.Equal(ChainAdvanceCode.ReviewerNotSeparated, decision.Code);
    Chain after = Must(await chains.GetAsync(chain.Id, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));
    Assert.Equal(ChainStatus.Parked, after.Status);
    Assert.Equal(2, after.ParkedAtPosition);
    // NO ATTEMPT SPENT AND NO CLAIM HELD: the step is still available to a corrected launch.
    Assert.Equal(0, after.StepAt(2)!.Attempts);
    Assert.Equal(ChainStepStatus.Pending, after.StepAt(2)!.Status);
  }

  [Fact]
  public async Task OnCompletionAsync_ADifferentReviewerModel_StartsTheReviewStep()
  {
    (ChainAdvanceService service, ChainService chains, FakeHost host, FakeAgents agents, Chain chain) =
        await NewAsync(ChainStepSeed.Implementing(1),
            new ChainStepSeed(2, ChainStepRole.Review("vendor/reviewer")));
    _ = await ClaimAsync(chains, chain, 1, Implementer);
    agents.Served[Implementer] = "vendor/implementer";

    ChainAdvance decision = Must(await service.OnCompletionAsync(Implementer, planId: 3, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Assert.Equal(ChainAdvanceKind.StartStep, decision.Kind);
    ChainStepAssignment assignment = Assert.Single(host.Started);
    Assert.Equal(2, assignment.Position);
    // THE REVIEWER'S MODEL TRAVELS (issue #218): the app is where a session's model is set, so an
    // assignment without it would leave the reviewer on the implementer's model.
    Assert.Equal("vendor/reviewer", assignment.Role!.Model);
    Assert.True(assignment.Role.IsReview);
  }

  [Fact]
  public async Task OnCompletionAsync_B6_AnOrdinaryChainStepIsUnaffected()
  {
    (ChainAdvanceService service, ChainService chains, FakeHost host, FakeAgents agents, Chain chain) =
        await NewAsync(ChainStepSeed.Implementing(1), ChainStepSeed.Implementing(2));
    _ = await ClaimAsync(chains, chain, 1, Implementer);
    agents.Served[Implementer] = "vendor/implementer";

    ChainAdvance decision = Must(await service.OnCompletionAsync(Implementer, planId: 3, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Assert.Equal(ChainAdvanceKind.StartStep, decision.Kind);
    ChainStepAssignment assignment = Assert.Single(host.Started);
    // An implementer step follows the session's model choice: no model is forced onto it.
    Assert.False(assignment.Role!.IsReview);
    Assert.Null(assignment.Role.Model);
  }

  [Fact]
  public async Task OnCompletionAsync_WithNoGateWired_AdvancesExactlyAsBefore()
  {
    // A container with no chain machinery (headless or test hosts) behaves exactly as it did
    // before this issue: no gate, no recorded model, no refusal.
    FakeStore store = new();
    FakeHost host = new();
    ChainService chains = new(store);
    Chain chain = Must(await chains.CreateAsync(
        ChainCreation.Implementing("C:\\ws", 3, 1, "t", "session-root", [1, 2]), T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    _ = await ClaimAsync(chains, chain, 1, Implementer);
    ChainAdvanceService service = new(chains, host);

    ChainAdvance decision = Must(await service.OnCompletionAsync(Implementer, planId: 3, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Assert.Equal(ChainAdvanceKind.StartStep, decision.Kind);
    _ = Assert.Single(host.Started);
  }
}
