namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>Re-review wave: the OLD NotSame asserts here proved nothing (two fresh
///     constructions are never the same instance regardless of resolution). The
///     resolution contract now lives in DispatcherResolutionContractTests, which
///     pins hook TARGETS by static method-group equality and the IsDoubleBacked
///     flag. This class keeps the ONE unique pin the old file had: the no-hook
///     production construction stays real (not double-backed).</summary>
public class RecordingDispatcherResolutionTests
{
  [Fact]
  public void ZeroHookConstruction_IsNotDoubleBacked_ProductionReal()
  {
    // The Program.cs construction (zero hooks) resolves the REAL NativeInput
    // surface on all five hooks - the production path restored by the re-review
    // resolution contract. Target equality is pinned in
    // DispatcherResolutionContractTests.ZeroHooks_ResolveAllFiveRealNativeInputHooks.
    PipeServer server = new(new BrokerConfig("ignored-pipe", "t"), foregroundPid: () => -1);
    Assert.False(server.InputDispatch.IsDoubleBacked);
  }
}
