using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>Issue #309 D5: the report REFUSES to exist until the thresholds are fixed.
///     <para>This is the acceptance criterion that makes the ordering checkable rather than
///     asserted: 'the bounds were chosen before the measurement' is a statement about the order
///     of two events, and the only way it can be verified is if a report CANNOT be produced
///     before the bounds exist. So the service reads the artifact first and fails without it.
///     </para></summary>
public class ChainTelemetryServiceTests
{
  private static readonly DateTimeOffset T0 = new(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);

  /// <summary>A threshold source whose answer the test states, including 'absent'.</summary>
  private sealed class FakeThresholds : IChainThresholdSource
  {
    public List<string> ReadKeys { get; } = [];

    public Result<ChainTelemetryThresholds>? Outcome { get; set; }

    public Task<Result<ChainTelemetryThresholds>> ReadAsync(string key, CancellationToken ct = default)
    {
      ReadKeys.Add(key);
      return Task.FromResult(Outcome ?? Result.Failure<ChainTelemetryThresholds>(
          new DomainError(ChainTelemetryThresholds.Code, "no artifact")));
    }
  }

  private sealed class FakeChains(params ChainTelemetryChain[] chains) : IChainTelemetrySource
  {
    public List<ChainTelemetryChain> Listed { get; } = [.. chains];

    public Task<Result<ChainTelemetryCatalog>> ListAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success(new ChainTelemetryCatalog(Listed, [])));
  }

  private sealed class FakeCosts : IChainSessionCostSource
  {
    public List<IReadOnlyList<string>> Requests { get; } = [];

    public Task<Result<IReadOnlyList<ChainTelemetrySessionCost>>> CostsForAsync(
        IReadOnlyList<string> sessionIds, CancellationToken ct = default)
    {
      Requests.Add(sessionIds);
      return Task.FromResult(Result.Success<IReadOnlyList<ChainTelemetrySessionCost>>([]));
    }
  }

  private sealed class FakeReviews : IChainReviewTelemetrySource
  {
    public Task<Result<ChainTelemetryReviewFacts>> ListAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success(ChainTelemetryReviewFacts.Empty));
  }

  private sealed class FakeBaseline(ChainTelemetryBaseline? baseline) : IChainBaselineSource
  {
    public Task<Result<ChainTelemetryBaseline?>> ReadAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success(baseline));
  }

  private static ChainTelemetryThresholds Limits() =>
      new()
      {
        InterventionShareMax = 0.25m,
        MedianCompletedStepsMin = 3m,
        ParkShareMax = 0.25m,
        TopParkReasonShareMax = 0.50m,
        FirstAttemptSuccessMin = 0.85m,
        CapReachedShareMax = 0.05m,
        ReviewDenyShareMin = 0.15m,
        ReviewDenyShareMax = 0.50m,
        PlanChangedAfterDenyMin = 0.80m,
        CostPerStepRatioMax = 1.0m,
        StartedSettledRatioMin = 0.80m,
        WallClockRatioMax = 1.0m,
      };

  private static ChainTelemetryChain Chain(int id, params ChainTelemetryStep[] steps) =>
      new(id, "Chain " + id, ChainStatus.Completed, null, T0, T0.AddHours(2), steps);

  private static ChainTelemetryStep Step(int position, string sessionId) =>
      new(position, ChainStepStatus.Done, sessionId, 1, 0, ChainStepRole.Implement, T0,
          T0.AddHours(1));

  [Fact]
  public async Task BuildAsync_WithoutAThresholdsArtifact_RefusesRatherThanReporting()
  {
    FakeThresholds thresholds = new();
    ChainTelemetryService service = new(new FakeChains(Chain(1, Step(1, "s1"))), new FakeCosts(),
        new FakeReviews(), new FakeBaseline(null), thresholds);

    Result<ChainTelemetryReport> report = await service.BuildAsync("Chain telemetry",
        TestContext.Current.CancellationToken);

    Assert.False(report.IsSuccess);
    Assert.Equal(ChainTelemetryThresholds.Code, report.Error.Code);
    // The refusal names the key it looked for, so the remedy is derivable from the error.
    Assert.Equal("sci.chain-telemetry/thresholds", Assert.Single(thresholds.ReadKeys));
  }

  [Fact]
  public async Task BuildAsync_ReadsTheThresholdsBeforeAnyMeasurement()
  {
    // THE ORDERING ITSELF, asserted: the thresholds read happens FIRST, so a report cannot be
    // computed from rows before the bounds exist. A service that gathered first and checked
    // after would satisfy every other test and violate D5.
    FakeThresholds thresholds = new() { Outcome = Result.Success(Limits()) };
    FakeChains chains = new(Chain(1, Step(1, "s1")));
    ChainTelemetryService service = new(chains, new FakeCosts(), new FakeReviews(),
        new FakeBaseline(null), thresholds);

    Result<ChainTelemetryReport> report = await service.BuildAsync("Chain telemetry",
        TestContext.Current.CancellationToken);

    Assert.True(report.IsSuccess, report.Error?.Message ?? "expected success");
    Assert.Equal("sci.chain-telemetry/thresholds", Assert.Single(thresholds.ReadKeys));
  }

  [Fact]
  public async Task BuildAsync_PricesOnlyTheSessionsTheChainsActuallyRecorded()
  {
    FakeCosts costs = new();
    ChainTelemetryService service = new(
        new FakeChains(Chain(1, Step(1, "s1"), Step(2, "s2")), Chain(2, Step(1, "s1"))),
        costs, new FakeReviews(), new FakeBaseline(null),
        new FakeThresholds { Outcome = Result.Success(Limits()) });

    _ = await service.BuildAsync("Chain telemetry", TestContext.Current.CancellationToken);

    // Deduplicated: two chains sharing a session must not price it twice, and a step with no
    // session contributes nothing to ask the ledger about.
    IReadOnlyList<string> asked = costs.Requests.Single();
    Assert.Equal(["s1", "s2"], asked.OrderBy(id => id, StringComparer.Ordinal));
  }

  [Fact]
  public async Task BuildAsync_PropagatesAReadFailure_RatherThanReportingOverHalfTheRows()
  {
    // A report computed over a silently missing half would present itself as complete, which
    // is worse than an error naming what could not be read.
    ChainTelemetryService service = new(new FailingChains(), new FakeCosts(), new FakeReviews(),
        new FakeBaseline(null), new FakeThresholds { Outcome = Result.Success(Limits()) });

    Result<ChainTelemetryReport> report = await service.BuildAsync("Chain telemetry",
        TestContext.Current.CancellationToken);

    Assert.False(report.IsSuccess);
    Assert.Equal("StorageUnavailable", report.Error.Code);
  }

  [Fact]
  public async Task BuildAsync_WithNoBaseline_StillReports_AndSaysNoRatioIsAsserted()
  {
    // The baseline is the one read whose absence is an ordinary answer: an empty ledger means
    // 'nothing to compare against', not a failure.
    ChainTelemetryService service = new(new FakeChains(Chain(1, Step(1, "s1"))), new FakeCosts(),
        new FakeReviews(), new FakeBaseline(null),
        new FakeThresholds { Outcome = Result.Success(Limits()) });

    Result<ChainTelemetryReport> report = await service.BuildAsync("Chain telemetry",
        TestContext.Current.CancellationToken);

    Assert.True(report.IsSuccess, report.Error?.Message ?? "expected success");
    Assert.Null(report.Value.Baseline);
    Assert.Contains(report.Value.Notes, note =>
        note.Contains("no subagent-era baseline", StringComparison.Ordinal));
  }

  /// <summary>A chain source that fails, for the propagation test.</summary>
  private sealed class FailingChains : IChainTelemetrySource
  {
    public Task<Result<ChainTelemetryCatalog>> ListAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Failure<ChainTelemetryCatalog>(
            new DomainError("StorageUnavailable", "the database is locked")));
  }
}
