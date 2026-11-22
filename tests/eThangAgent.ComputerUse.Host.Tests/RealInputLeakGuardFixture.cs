namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>The assembly-level guard that turns 'a test injected real input into the
///     developer's desktop' into a NAMED failure.
///     <para>The defect it exists for: a PipeServer built with ZERO send hooks resolves the
///     REAL NativeInput surface - that is the production wiring, and it is correct. A test
///     that builds one and then dispatches an input method therefore types into whatever
///     window has focus and scrolls whatever is under the cursor. Nothing failed: the
///     suite reported green while a test's text appeared in a live browser and a stray
///     keystroke moved someone's page.</para>
///     <para>WHY THIS SHAPE. Every real send funnels through NativeInput.Inject, so one
///     counter covers the whole live-fire surface - chords, text, clicks, wheels, drags,
///     and the paste Ctrl+V alike. The fixture snapshots it when the assembly starts and
///     re-reads it when the assembly ends: any growth is real input that left the process,
///     and no per-test bookkeeping can miss it. A test that legitimately needs to prove the
///     real path is reachable asserts on the resolved DELEGATE (method-group equality),
///     which dispatches nothing.</para></summary>
#pragma warning disable CA1515 // Named decision: xUnit requires an assembly fixture type to be public.
public sealed class RealInputLeakGuardFixture : IAsyncLifetime
#pragma warning restore CA1515
{
  /// <summary>The injection count observed when the assembly started.</summary>
  private int Baseline { get; set; }

  public RealInputLeakGuardFixture()
    : this(() => NativeInput.RealInjections)
  {
  }

  /// <summary>The seam the guard's own tests drive: a counter they control, so the guard's
  ///     verdict is provable without injecting anything.</summary>
  internal RealInputLeakGuardFixture(Func<int> injections) => Injections = injections;

  /// <summary>The counter this guard watches: the process's real-injection count in
  ///     production, a controlled value in the guard's own tests.</summary>
  private Func<int> Injections { get; }

  /// <summary>How many real injections this assembly observed.</summary>
  internal int InjectionsObserved => Injections() - Baseline;

  public ValueTask InitializeAsync()
  {
    Baseline = Injections();
    return ValueTask.CompletedTask;
  }

  public ValueTask DisposeAsync()
  {
    ThrowIfInjected(InjectionsObserved);
    return ValueTask.CompletedTask;
  }

  private static void ThrowIfInjected(int injected)
  {
    if (injected > 0)
    {
      throw new InvalidOperationException(
        $"this test assembly injected {injected} real input batch(es) into the live desktop. "
        + "A PipeServer built with zero send hooks resolves the REAL NativeInput surface, so "
        + "any input method dispatched through it types into the focused window and scrolls "
        + "under the cursor. Build the server through FakeConnectionFactory (recording "
        + "doubles) or pass a complete InputHooks set; assert the real path by delegate "
        + "equality instead of dispatching.");
    }
  }
}
