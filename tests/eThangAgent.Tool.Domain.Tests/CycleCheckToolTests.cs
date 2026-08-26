namespace eThangAgent.ToolDomain.Tests;

public class CycleCheckToolTests
{
  private static ToolResult Run(string json) =>
    new CycleCheckTool().ExecuteAsync(new RawToolInput("cycle_check", json)).GetAwaiter().GetResult();

  // TLC-derived regression (DiResolution.tla): F->R->A->S->W->T->E->F with an
  // eager closing edge must be reported as a deadlock, not silently accepted.
  [Fact]
  public void EagerCycle_FromTlcCounterexample_ReportsDeadlockRisk()
  {
    List<DependencyEdge> edges = [.. Chain("F", "R", "A", "S", "W", "T", "E")
, new DependencyEdge("E", "F", Deferred: false)];

    CycleReport report = CycleDetector.Detect(["F"], edges).Value!;

    _ = Assert.Single(report.ReachableCycles);
    Assert.Equal(CycleVerdict.DeadlockRisk, report.ReachableCycles[0].Verdict);
  }

  [Fact]
  public void FullyDeferredCycle_ReportsLatent()
  {
    List<DependencyEdge> edges =
        [
            new("A", "B", true), new("B", "C", true), new("C", "A", true),
        ];

    CycleReport report = CycleDetector.Detect(["A"], edges).Value!;

    DetectedCycle cycle = Assert.Single(report.ReachableCycles);
    Assert.Equal(CycleVerdict.Latent, cycle.Verdict);
  }

  [Fact]
  public void SelfLoop_Eager_IsDeadlockRisk()
  {
    CycleReport report = CycleDetector.Detect(["X"], [new DependencyEdge("X", "X", false)]).Value!;

    DetectedCycle cycle = Assert.Single(report.ReachableCycles);
    Assert.Equal(["X"], cycle.Members);
    Assert.Equal(CycleVerdict.DeadlockRisk, cycle.Verdict);
  }

  [Fact]
  public void CycleOutsideEntryReach_CountedUnreachable()
  {
    // Entry D reaches only itself (acyclic); the A/B cycle is unreachable.
    List<DependencyEdge> edges =
        [
            new("A", "B", false), new("B", "A", false), new("D", "E", false),
        ];

    CycleReport report = CycleDetector.Detect(["D"], edges).Value!;

    Assert.Empty(report.ReachableCycles);
    Assert.Equal(1, report.UnreachableCycles);
  }

  // A cycle mixing eager and deferred edges cannot walk itself during construction:
  // crossing the deferred edge requires an explicit later invocation, by which time
  // construction of the original unit has finished. Only all-eager cycles deadlock.
  [Fact]
  public void MixedCycle_IsLatent()
  {
    List<DependencyEdge> edges = [new("A", "B", true), new("B", "A", false)];

    CycleReport report = CycleDetector.Detect([], edges).Value!;

    Assert.Equal(CycleVerdict.Latent, Assert.Single(report.ReachableCycles).Verdict);
  }

  [Fact]
  public void NoEntries_ChecksWholeGraph()
  {
    List<DependencyEdge> edges = [new("A", "B", false), new("B", "A", false)];

    CycleReport report = CycleDetector.Detect([], edges).Value!;

    Assert.Equal(CycleVerdict.DeadlockRisk, Assert.Single(report.ReachableCycles).Verdict);
  }

  [Fact]
  public void AcyclicChain_ReportsClean()
  {
    CycleReport report = CycleDetector.Detect(["A"], [.. Chain("A", "B", "C")]).Value!;

    Assert.True(report.IsClean);
    Assert.Equal(0, report.UnreachableCycles);
  }

  [Fact]
  public void Execute_HappyPath_ProducesContractFormat()
  {
    string json = /*lang=json,strict*/ """{"edges":[{"from":"A","to":"B","deferred":false},{"from":"B","to":"A","deferred":false}],"entry":["A"],"timeoutSeconds":30}""";

    ToolResult result = Run(json);

    Assert.False(result.IsError);
    Assert.StartsWith("[cycle-check: 2 units, 2 edges, 1 entry point]", result.Content, StringComparison.Ordinal);
    Assert.Contains("[cycle] A -> B -> A — contains all-eager cycle: deadlock-risk", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Execute_CleanGraph_ReportsOk()
  {
    string json = /*lang=json,strict*/ """{"edges":[{"from":"A","to":"B","deferred":false}],"timeoutSeconds":30}""";

    ToolResult result = Run(json);

    Assert.False(result.IsError);
    Assert.Contains("[cycle-check: 2 units, 1 edges, 0 entry points]", result.Content, StringComparison.Ordinal);
    Assert.Contains("[ok] no dependency cycles", result.Content, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("{}")]
  [InlineData(/*lang=json,strict*/ "{\"edges\":\"x\",\"timeoutSeconds\":30}")]
  [InlineData(/*lang=json,strict*/ "{\"edges\":[],\"timeoutSeconds\":30}")]
  [InlineData(/*lang=json,strict*/ "{\"edges\":[{\"from\":\"A\"}],\"timeoutSeconds\":30}")]
  [InlineData(/*lang=json,strict*/ "{\"edges\":[{\"from\":\"A\",\"to\":\"A\",\"deferred\":\"yes\"}],\"timeoutSeconds\":30}")]
  [InlineData(/*lang=json,strict*/ "{\"edges\":[{\"from\":\"\",\"to\":\"B\",\"deferred\":false}],\"timeoutSeconds\":30}")]
  [InlineData(/*lang=json,strict*/ "{\"edges\":[],\"entry\":\"A\",\"timeoutSeconds\":30}")]
  [InlineData(/*lang=json,strict*/ "{\"edges\":[],\"entry\":[\"\"],\"timeoutSeconds\":30}")]
  public void Execute_MalformedInput_ReturnsTypedError(string json)
  {
    ToolResult result = Run(json);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [", result.Content, StringComparison.Ordinal);
  }

  private static IEnumerable<DependencyEdge> Chain(params string[] units)
  {
    for (int i = 0; i < units.Length - 1; i++)
    {
      yield return new DependencyEdge(units[i], units[i + 1], Deferred: false);
    }
  }
}
