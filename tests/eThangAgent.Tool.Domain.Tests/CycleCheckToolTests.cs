using System.Text.Json;
using eThangAgent.ToolDomain;

namespace eThangAgent.ToolDomain.Tests;

public class CycleCheckToolTests
{
    private readonly CycleCheckTool _tool = new();

    private static ToolResult Run(string json) =>
        new CycleCheckTool().ExecuteAsync(new RawToolInput("cycle_check", json)).GetAwaiter().GetResult();

    // TLC-derived regression (DiResolution.tla): F->R->A->S->W->T->E->F with an
    // eager closing edge must be reported as a deadlock, not silently accepted.
    [Fact]
    public void EagerCycle_FromTlcCounterexample_ReportsDeadlockRisk()
    {
        var edges = Chain("F","R","A","S","W","T","E")
            .Append(new DependencyEdge("E", "F", Deferred: false))
            .ToList();

        var report = CycleDetector.Detect(["F"], edges).Value!;

        Assert.Single(report.ReachableCycles);
        Assert.Equal(CycleVerdict.DeadlockRisk, report.ReachableCycles[0].Verdict);
    }

    [Fact]
    public void FullyDeferredCycle_ReportsLatent()
    {
        var edges = new List<DependencyEdge>
        {
            new("A", "B", true), new("B", "C", true), new("C", "A", true),
        };

        var report = CycleDetector.Detect(["A"], edges).Value!;

        var cycle = Assert.Single(report.ReachableCycles);
        Assert.Equal(CycleVerdict.Latent, cycle.Verdict);
    }

    [Fact]
    public void SelfLoop_Eager_IsDeadlockRisk()
    {
        var report = CycleDetector.Detect(["X"], [new DependencyEdge("X", "X", false)]).Value!;

        var cycle = Assert.Single(report.ReachableCycles);
        Assert.Equal(["X"], cycle.Members);
        Assert.Equal(CycleVerdict.DeadlockRisk, cycle.Verdict);
    }

    [Fact]
    public void CycleOutsideEntryReach_CountedUnreachable()
    {
        // Entry D reaches only itself (acyclic); the A/B cycle is unreachable.
        var edges = new List<DependencyEdge>
        {
            new("A", "B", false), new("B", "A", false), new("D", "E", false),
        };

        var report = CycleDetector.Detect(["D"], edges).Value!;

        Assert.Empty(report.ReachableCycles);
        Assert.Equal(1, report.UnreachableCycles);
    }

    // A cycle mixing eager and deferred edges cannot walk itself during construction:
    // crossing the deferred edge requires an explicit later invocation, by which time
    // construction of the original unit has finished. Only all-eager cycles deadlock.
    [Fact]
    public void MixedCycle_IsLatent()
    {
        var edges = new List<DependencyEdge> { new("A", "B", true), new("B", "A", false) };

        var report = CycleDetector.Detect([], edges).Value!;

        Assert.Equal(CycleVerdict.Latent, Assert.Single(report.ReachableCycles).Verdict);
    }

    [Fact]
    public void NoEntries_ChecksWholeGraph()
    {
        var edges = new List<DependencyEdge> { new("A", "B", false), new("B", "A", false) };

        var report = CycleDetector.Detect([], edges).Value!;

        Assert.Equal(CycleVerdict.DeadlockRisk, Assert.Single(report.ReachableCycles).Verdict);
    }

    [Fact]
    public void AcyclicChain_ReportsClean()
    {
        var report = CycleDetector.Detect(["A"], Chain("A", "B", "C").ToList()).Value!;

        Assert.True(report.IsClean);
        Assert.Equal(0, report.UnreachableCycles);
    }

    [Fact]
    public void Execute_HappyPath_ProducesContractFormat()
    {
        var json = """{"edges":[{"from":"A","to":"B","deferred":false},{"from":"B","to":"A","deferred":false}],"entry":["A"],"timeoutSeconds":30}""";

        var result = Run(json);

        Assert.False(result.IsError);
        Assert.StartsWith("[cycle-check: 2 units, 2 edges, 1 entry point]", result.Content);
        Assert.Contains("[cycle] A -> B -> A — contains all-eager cycle: deadlock-risk", result.Content);
    }

    [Fact]
    public void Execute_CleanGraph_ReportsOk()
    {
        var json = """{"edges":[{"from":"A","to":"B","deferred":false}],"timeoutSeconds":30}""";

        var result = Run(json);

        Assert.False(result.IsError);
        Assert.Contains("[cycle-check: 2 units, 1 edges, 0 entry points]", result.Content);
        Assert.Contains("[ok] no dependency cycles", result.Content);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"edges\":\"x\",\"timeoutSeconds\":30}")]
    [InlineData("{\"edges\":[],\"timeoutSeconds\":30}")]
    [InlineData("{\"edges\":[{\"from\":\"A\"}],\"timeoutSeconds\":30}")]
    [InlineData("{\"edges\":[{\"from\":\"A\",\"to\":\"A\",\"deferred\":\"yes\"}],\"timeoutSeconds\":30}")]
    [InlineData("{\"edges\":[{\"from\":\"\",\"to\":\"B\",\"deferred\":false}],\"timeoutSeconds\":30}")]
    [InlineData("{\"edges\":[],\"entry\":\"A\",\"timeoutSeconds\":30}")]
    [InlineData("{\"edges\":[],\"entry\":[\"\"],\"timeoutSeconds\":30}")]
    public void Execute_MalformedInput_ReturnsTypedError(string json)
    {
        var result = Run(json);

        Assert.True(result.IsError);
        Assert.StartsWith("Error [", result.Content);
    }

    private static IEnumerable<DependencyEdge> Chain(params string[] units)
    {
        for (var i = 0; i < units.Length - 1; i++)
            yield return new DependencyEdge(units[i], units[i + 1], Deferred: false);
    }
}
