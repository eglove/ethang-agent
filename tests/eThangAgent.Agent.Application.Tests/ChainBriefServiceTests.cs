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
  public async Task BuildLaunch_CarriesTheContractDerivedFromTheSameFacts()
  {
    // Issue #311: the launch is the seed AND the contract, from ONE gather — so the ledger key
    // and the gate in the prompt are the values the brief was rendered with.
    ChainBriefService service = new(new FakeSource());

    Result<ChainBriefLaunch> launch = await service.BuildLaunchAsync(Assignment, TestContext.Current.CancellationToken);

    Assert.True(launch.IsSuccess, launch.Error?.Message);
    Message seeded = Assert.Single(launch.Value.Seed);
    Assert.StartsWith(ChainBriefRenderer.Prefix, seeded.Content, StringComparison.Ordinal);
    ChainContract contract = Assert.IsType<ChainContract>(launch.Value.Contract);
    Assert.Equal(7, contract.ChainId);
    Assert.Equal(104, contract.PlanId);
    Assert.Equal(2, contract.Position);
    Assert.Equal("sci.dashboard-truth/ledger", contract.LedgerKey);
    // The brief and the contract name the same key — the point of one derivation site.
    Assert.Contains(contract.LedgerKey, seeded.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task BuildSeed_IsTheSeedHalfOfBuildLaunch()
  {
    // The seed path is not a second renderer: it delegates, so a future change to the launch
    // cannot leave the two disagreeing.
    ChainBriefService service = new(new FakeSource());

    Result<IReadOnlyList<Message>> seed = await service.BuildSeedAsync(Assignment, TestContext.Current.CancellationToken);
    Result<ChainBriefLaunch> launch = await service.BuildLaunchAsync(Assignment, TestContext.Current.CancellationToken);

    Assert.Equal(Assert.Single(launch.Value!.Seed).Content, Assert.Single(seed.Value!).Content,
        StringComparer.Ordinal);
  }

  [Fact]
  public async Task BuildLaunch_WhenThePlanStepIsGone_RefusesWithTheSameNamedCode()
  {
    ChainBriefService service = new(new FakeSource(plan: FakeSource.PlanWithout()));

    Result<ChainBriefLaunch> launch = await service.BuildLaunchAsync(Assignment, TestContext.Current.CancellationToken);

    Assert.False(launch.IsSuccess);
    Assert.Equal("ChainStepMissing", launch.Error.Code);
  }

  [Fact]
  public async Task BuildSeed_ReportsPredecessorsFromTheChain()
  {
    ChainBriefService service = new(new FakeSource(chain: Result.Success(new ChainBriefChain(
        7, "Dashboard truth", 104, [new ChainBriefPredecessor(1, ChainStepStatus.Done, "status line rewritten")]))));

    Message message = Assert.Single((await service.BuildSeedAsync(Assignment, TestContext.Current.CancellationToken)).Value!);

    Assert.Contains("Step 1 — Done: status line rewritten", message.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task BuildSeed_ForAReviewAssignment_TellsTheSessionToJudge()
  {
    // The role reaches the brief from the ASSIGNMENT when the launcher read it (issue #306).
    ChainBriefService service = new(new FakeSource());
    ChainStepAssignment review = Assignment.WithRole(ChainStepRole.Review("vendor/reviewer"));

    Message message = Assert.Single(
        (await service.BuildSeedAsync(review, TestContext.Current.CancellationToken)).Value!);

    Assert.Contains("You are the REVIEWER for this step", message.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("## How to finish\n\nMark step 2 of plan #104 done:",
        message.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task BuildSeed_ForAReviewStep_AsksTheCatalogForTheRubric()
  {
    FakeSource source = new(rubricAvailable: true);
    ChainBriefService service = new(source);

    Message message = Assert.Single((await service
        .BuildSeedAsync(Assignment.WithRole(ChainStepRole.Review()), TestContext.Current.CancellationToken)).Value!);

    Assert.Equal(1, source.RubricReads);
    Assert.Contains("Load the `reviewing-plans` skill", message.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task BuildSeed_ForAnImplementStep_NeverAsksTheCatalogForTheRubric()
  {
    // A chain with no review step must cost exactly what it did before this issue.
    FakeSource source = new();
    ChainBriefService service = new(source);

    _ = await service.BuildSeedAsync(Assignment, TestContext.Current.CancellationToken);

    Assert.Equal(0, source.RubricReads);
  }

  [Fact]
  public async Task BuildSeed_WhenTheAssignmentNamesNoRole_FallsBackToTheRecordsRole()
  {
    // A launcher that has not read the chain still gets the record's answer rather than a
    // silent 'implement' - the fallback that keeps a review step from degrading to a work step.
    FakeSource source = new(chain: Result.Success(new ChainBriefChain(
        7, "2026-08-24-dashboard-truth", 104, [], ChainStepRole.Review())));
    ChainBriefService service = new(source);

    Message message = Assert.Single(
        (await service.BuildSeedAsync(Assignment, TestContext.Current.CancellationToken)).Value!);

    Assert.Contains("You are the REVIEWER for this step", message.Content, StringComparison.Ordinal);
  }

  private sealed class FakeSource(
      Result<ChainBriefChain>? chain = null,
      Result<ChainBriefPlan>? plan = null,
      Result<IReadOnlyList<string>>? gate = null,
      bool rubricAvailable = false) : IChainBriefSource
  {
    public int Reads { get; private set; }

    /// <summary>How many times the skill catalog was consulted (issue #306): an implement step
    ///     must never ask, so the count is the assertion that the read is conditional.</summary>
    public int RubricReads { get; private set; }

    public static Result<ChainBriefPlan> PlanWithout() =>
        Result.Success(new ChainBriefPlan(104, 3, "Make the dashboard tell the truth.",
            [new ChainBriefPlanStep(1, "Only step left", null)]));

    public Task<Result<ChainBriefChain>> GetChainAsync(int chainId, int position,
        CancellationToken ct = default)
    {
      Reads++;
      return Task.FromResult(chain ?? Result.Success(new ChainBriefChain(
          7, "2026-08-24-dashboard-truth", 104, [])));
    }

    public Task<Result<bool>> GetReviewRubricAvailableAsync(CancellationToken ct = default)
    {
      RubricReads++;
      return Task.FromResult(Result.Success(rubricAvailable));
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
