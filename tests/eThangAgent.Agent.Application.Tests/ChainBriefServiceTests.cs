using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>The brief service (issue #302): gather, render, seed. The source is faked because
///     these tests are about the SERVICE's contract — which reads it makes, what it does with
///     a refusal — not about any store.</summary>
public sealed class ChainBriefServiceTests
{
  private static readonly ChainStepAssignment Assignment = new(ChainId: 7, PlanId: 104, Position: 2, SessionId: null);

  [Fact]
  public async Task BuildSeed_ProducesOneSummaryMessageCarryingTheRenderedBrief()
  {
    ChainBriefService service = new(new FakeSource());

    Result<IReadOnlyList<Message>> seed = await service.BuildSeedAsync(Assignment, TestContext.Current.CancellationToken);

    Assert.True(seed.IsSuccess, seed.Error?.Message);
    Message message = Assert.Single(seed.Value);
    // A summary message, not user voice: the model must read it as harness context.
    Assert.True(message.IsSummary);
    Assert.Equal(Role.System, message.Role);
    Assert.StartsWith(ChainBriefRenderer.Prefix, message.Content, StringComparison.Ordinal);
    Assert.Contains("You are step 2 of chain #7", message.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task BuildSeed_NamesThePrefixExactlyOnce()
  {
    // The renderer emits the prefix as the brief's first line; the seed must not add it again.
    ChainBriefService service = new(new FakeSource());

    Message message = Assert.Single((await service.BuildSeedAsync(Assignment, TestContext.Current.CancellationToken)).Value!);

    int occurrences = message.Content.Split(ChainBriefRenderer.Prefix).Length - 1;
    Assert.Equal(1, occurrences);
  }

  [Fact]
  public async Task BuildSeed_DerivesTheLedgerKeyFromTheChainTitle()
  {
    ChainBriefService service = new(new FakeSource());

    Message message = Assert.Single((await service.BuildSeedAsync(Assignment, TestContext.Current.CancellationToken)).Value!);

    Assert.Contains("sci.dashboard-truth/ledger", message.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task BuildSeed_WhenThePlanStepIsGone_RefusesWithTheNamedCode()
  {
    // The plan was revised: its step 2 no longer exists, so the chain step points at nothing.
    ChainBriefService service = new(new FakeSource(plan: FakeSource.PlanWithout()));

    Result<IReadOnlyList<Message>> seed = await service.BuildSeedAsync(Assignment, TestContext.Current.CancellationToken);

    Assert.False(seed.IsSuccess);
    Assert.Equal("ChainStepMissing", seed.Error.Code);
  }

  [Fact]
  public async Task BuildSeed_WhenTheChainIsMissing_SurfacesTheStoreError()
  {
    ChainBriefService service = new(new FakeSource(chain: Result.Failure<ChainBriefChain>(
        new DomainError("ChainNotFound", "no chain with id 7 in this workspace."))));

    Result<IReadOnlyList<Message>> seed = await service.BuildSeedAsync(Assignment, TestContext.Current.CancellationToken);

    Assert.False(seed.IsSuccess);
    Assert.Equal("ChainNotFound", seed.Error.Code);
  }

  [Fact]
  public async Task BuildSeed_WhenTheSourceCannotReadTheGate_StillRefusesRatherThanGuessing()
  {
    ChainBriefService service = new(new FakeSource(gate: Result.Failure<IReadOnlyList<string>>(
        new DomainError("StateUnavailable", "the state store is unreadable."))));

    Result<IReadOnlyList<Message>> seed = await service.BuildSeedAsync(Assignment, TestContext.Current.CancellationToken);

    Assert.False(seed.IsSuccess);
    Assert.Equal("StateUnavailable", seed.Error.Code);
  }

  [Fact]
  public async Task BuildSeed_WithANonExecutableAssignment_RefusesBeforeReadingAnything()
  {
    FakeSource source = new();
    ChainBriefService service = new(source);

    Result<IReadOnlyList<Message>> seed = await service.BuildSeedAsync(
        new ChainStepAssignment(0, 0, 0, null), TestContext.Current.CancellationToken);

    Assert.False(seed.IsSuccess);
    Assert.Equal("InvalidAssignment", seed.Error.Code);
    Assert.Equal(0, source.Reads);
  }

  [Fact]
  public async Task BuildSeed_ReportsPredecessorsFromTheChain()
  {
    ChainBriefService service = new(new FakeSource(chain: Result.Success(new ChainBriefChain(
        7, "Dashboard truth", 104, [new ChainBriefPredecessor(1, ChainStepStatus.Done, "status line rewritten")]))));

    Message message = Assert.Single((await service.BuildSeedAsync(Assignment, TestContext.Current.CancellationToken)).Value!);

    Assert.Contains("Step 1 — Done: status line rewritten", message.Content, StringComparison.Ordinal);
  }

  private sealed class FakeSource(
      Result<ChainBriefChain>? chain = null,
      Result<ChainBriefPlan>? plan = null,
      Result<IReadOnlyList<string>>? gate = null) : IChainBriefSource
  {
    public int Reads { get; private set; }

    public static Result<ChainBriefPlan> PlanWithout() =>
        Result.Success(new ChainBriefPlan(104, 3, "Make the dashboard tell the truth.",
            [new ChainBriefPlanStep(1, "Only step left", null)]));

    public Task<Result<ChainBriefChain>> GetChainAsync(int chainId, CancellationToken ct = default)
    {
      Reads++;
      return Task.FromResult(chain ?? Result.Success(new ChainBriefChain(
          7, "2026-08-24-dashboard-truth", 104, [])));
    }

    public Task<Result<ChainBriefPlan>> GetPlanAsync(int planId, CancellationToken ct = default)
    {
      Reads++;
      return Task.FromResult(plan ?? Result.Success(new ChainBriefPlan(104, 3,
          "Make the dashboard tell the truth about what it did.",
          [new ChainBriefPlanStep(1, "Honest status line", null),
           new ChainBriefPlanStep(2, "Real demo broker", null)])));
    }

    public Task<Result<IReadOnlyList<string>>> GetGateCommandsAsync(CancellationToken ct = default)
    {
      Reads++;
      return Task.FromResult(gate ?? Result.Success<IReadOnlyList<string>>(["dotnet build"]));
    }
  }
}
