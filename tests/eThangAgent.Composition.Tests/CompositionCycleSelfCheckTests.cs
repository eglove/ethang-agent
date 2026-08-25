using eThangAgent.ToolDomain;

namespace eThangAgent.Composition.Tests;

/// <summary>Self-check: the tool's own composition graph, expressed as the same edge
///     list cycle_check consumes, must never contain an all-eager cycle. This is the
///     TLC-proven deadlock class (DiResolution.tla): agent surface -> spawn -> tool
///     registry -> exec tool -> IExecEngine -> back to the Func registry. The two edges
///     that close it (Func -> registry construction, and IExecEngine's per-execution
///     registry lookup) are deferral boundaries; if either ever turns eager again, this
///     test fails before the hang can ship.</summary>
public class CompositionCycleSelfCheckTests
{
    [Fact]
    public void OwnComposition_HasNoAllEagerCycles()
    {
        // Units mirror AgentComposition registrations and their construction edges.
        DependencyEdge E(string from, string to, bool deferred = false) => new(from, to, deferred);
        var edges = new List<DependencyEdge>
        {
            E("FuncRegistry", "CapabilityRegistry", deferred: true),      // closure constructs on invoke
            E("CapabilityRegistry", "AgentCapabilityProvider"),
            E("AgentCapabilityProvider", "StartSpawnHandler"),
            E("StartSpawnHandler", "SubAgentSpawner"),
            E("SubAgentSpawner", "ToolRegistry"),
            E("ToolRegistry", "ExecTool"),
            E("ExecTool", "IExecEngine"),
            E("IExecEngine", "FuncRegistry", deferred: true),             // resolved per execution
            E("ToolRegistry", "ClarifyTool"),
            E("AgentSurface", "CapabilityRegistry"),
            E("Session", "ModelProviderFactory"),
        };

        var report = CycleDetector.Detect(["Session"], edges).Value!;

        Assert.All(report.ReachableCycles,
            c => Assert.NotEqual(CycleVerdict.DeadlockRisk, c.Verdict));
    }
}