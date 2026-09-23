
namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>Final review wave (NEW-1): a PipeServer constructed with ANY injected
///     send hook must resolve a double-backed dispatcher. The WithRecordingClick
///     factory omitted sendButton, so the ctor's ALL-hooks predicate fell through
///     to InputDispatch.Create - the REAL dispatcher - and every wire test built on
///     it dispatched real SendInput. This test proves the resolved dispatcher is
///     double-backed by construction: the input dispatcher is the ctor's
///     double-backed instance whenever any hook is injected.</summary>
public class RecordingDispatcherResolutionTests
{
  [Fact]
  public void WithRecordingClick_ResolvesADoubleBackedDispatcher_NotTheRealCreate()
  {
    PipeServer server = FakeConnectionFactory.WithRecordingClick(foreground: 4242);
    Assert.NotSame(InputDispatch.Create(() => 4242), server.InputDispatch);
  }

  [Fact]
  public void AnyInjectedHook_NeverFallsBackToTheRealDispatcher()
  {
    // One hook alone (the pre-fix shape: hooks present but sendButton missing)
    // must still resolve double-backed - a partial injection is the common case.
    PipeServer server = new(new BrokerConfig("ignored-pipe", "t"), foregroundPid: () => 1,
      sendDrag: static (_, _, _, _, _) => true,
      sendWheelAt: static (_, _, _, _) => true);
    Assert.NotSame(InputDispatch.Create(() => 1), server.InputDispatch);
  }

  [Fact]
  public void ZeroHooks_KeepsTheRealProductionDispatcher()
  {
    // The no-hook default IS the real dispatcher (production path) - pinned so the
    // fallback removal cannot silently flip the production default.
    PipeServer server = new(new BrokerConfig("ignored-pipe", "t"), foregroundPid: () => -1);
    Assert.False(server.InputDispatch.IsDoubleBacked); // the no-hook hooks ARE the real NativeInput defaults
    Assert.False(server.InputDispatch.IsDoubleBacked);
  }
}
