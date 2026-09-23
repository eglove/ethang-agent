namespace eThangAgent.ComputerUse.ACL.Tests;

#pragma warning disable CA2007 // Named decision: xUnit test methods, no SynchronizationContext.
/// <summary>Regression: user32 exports only the suffixed DispatchMessageA/W - the bare
///     DispatchMessage P/Invoke threw EntryPointNotFoundException on the pump thread's
///     first dispatched message. The STA thread died (destroying the fixture window
///     with it) while the fault sat in a local nobody read, so every integration test
///     failed as an opaque "no live test window". This test posts a real message and
///     asserts the pump survives the dispatch.</summary>
public sealed class Win32TestWindowPumpTests
{
  [Fact]
  public async Task Pump_SurvivesDispatchingAPostedMessage()
  {
    using Win32TestWindow window = new();
    window.PostProbeMessage();
    await Task.Delay(1000, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(window.IsWindowAliveNow(),
        "the pump thread died (its death destroys the window): " + window.PumpDiagnostics);
    Assert.StartsWith("no fault", window.PumpDiagnostics, StringComparison.Ordinal);
  }
}
