namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>The real-input leak guard's own contract: it fails when the injection counter
///     grew and stays silent when it did not. Both are driven through the counter seam, so
///     the guard is proven without injecting anything into the live desktop.</summary>
public class RealInputLeakGuardTests
{
  [Fact]
  public async Task NoInjection_GuardIsSilent()
  {
    int counter = 7;
    RealInputLeakGuardFixture guard = new(() => counter);
    await guard.InitializeAsync().ConfigureAwait(true);
    Assert.Equal(0, guard.InjectionsObserved); // nothing injected, so nothing grew
    await guard.DisposeAsync().ConfigureAwait(true); // must not throw
  }

  [Fact]
  public async Task AnyInjection_FailsTheAssembly_NamingTheCount()
  {
    int counter = 0;
    // Named decision (CA2000): DisposeAsync IS the dispose call under test - it throws
    // instead of returning, which is the whole point, so no using form can satisfy the rule.
#pragma warning disable CA2000 // Dispose objects before losing scope
    RealInputLeakGuardFixture guard = new(() => counter);
#pragma warning restore CA2000
    await guard.InitializeAsync().ConfigureAwait(true);
    counter = 3;
    InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
      () => guard.DisposeAsync().AsTask()).ConfigureAwait(true);
    Assert.Contains("3 real input", failure.Message, StringComparison.Ordinal);
    Assert.Contains("zero send hooks", failure.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void RealCounter_IsReadable_AndNeverNegative() =>
    // The production counter the assembly fixture reads; a read must never fault.
    Assert.True(NativeInput.RealInjections >= 0);
}
