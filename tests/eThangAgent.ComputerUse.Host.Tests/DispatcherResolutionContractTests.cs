namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>Re-review wave: the resolution CONTRACT. Zero injected hooks => ALL
///     five hooks wire to the real NativeInput surface (production path restored -
///     Program.cs constructs with zero hooks). ANY hook injected => ALL five are
///     REQUIRED; a partial set throws ArgumentException naming the missing hook
///     (partial injection is what let wire tests live-fire). The pins assert
///     RESOLUTION ONLY - static-method-group delegate equality and the
///     IsDoubleBacked flag - never a real dispatch.</summary>
public class DispatcherResolutionContractTests
{
  [Fact]
  public void PartialInjection_ThrowsArgumentException_NamingTheMissingHook()
  {
    // sendChord alone is a partial set: the other four hooks are missing.
    ArgumentException invalid = Assert.Throws<ArgumentException>(() => new PipeServer(
        new BrokerConfig("ignored-pipe", "t"), foregroundPid: () => 1, sendChord: static _ => true));
    Assert.Contains("sendText", invalid.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void PartialInjection_NamesEveryMissingHook()
  {
    // chord + wheel present: text, drag, and the positioned button are missing.
    ArgumentException invalid = Assert.Throws<ArgumentException>(() => new PipeServer(
        new BrokerConfig("ignored-pipe", "t"), foregroundPid: () => 1,
        sendChord: static _ => true, sendWheelAt: static (_, _, _, _) => true));
    Assert.Contains("sendText", invalid.Message, StringComparison.Ordinal);
    Assert.Contains("sendDrag", invalid.Message, StringComparison.Ordinal);
    Assert.Contains("sendMouseButtonAt", invalid.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void ZeroHooks_ResolveAllFiveRealNativeInputHooks()
  {
    // The Program.cs construction: zero hooks => the REAL surface (production
    // restored) and NOT the double-backed test surface. Delegate equality against
    // the NativeInput static method groups proves each hook's target without
    // dispatching anything.
    PipeServer server = new(new BrokerConfig("ignored-pipe", "t"), foregroundPid: () => -1);
    Assert.False(server.InputDispatch.IsDoubleBacked);
    Assert.Equal(NativeInputChord(), server.InputDispatch.ChordHook);
    Assert.Equal(NativeInputText(), server.InputDispatch.TextHook);
    Assert.Equal(NativeInputDrag(), server.InputDispatch.DragHook);
    Assert.Equal(NativeInputButtonAt(), server.InputDispatch.ButtonAtHook);
    Assert.Equal(NativeInputWheelAt(), server.InputDispatch.WheelAtHook);
  }

  [Fact]
  public void FullDoubleSet_ResolvesAllFiveDoubles_DoubleBacked()
  {
    PipeServer server = FakeConnectionFactory.WithRecordingClick(foreground: 4242);
    Assert.True(server.InputDispatch.IsDoubleBacked);
    // None of the hooks may equal the real NativeInput method groups: a double set
    // where any hook secretly resolved real would be exactly the leak this
    // contract closes. Equality probes only - no dispatch.
    Assert.NotEqual(NativeInputChord(), server.InputDispatch.ChordHook);
    Assert.NotEqual(NativeInputText(), server.InputDispatch.TextHook);
    Assert.NotEqual(NativeInputDrag(), server.InputDispatch.DragHook);
    Assert.NotEqual(NativeInputButtonAt(), server.InputDispatch.ButtonAtHook);
    Assert.NotEqual(NativeInputWheelAt(), server.InputDispatch.WheelAtHook);
  }

  [Fact]
  public void RealCreate_ResolvesAllFiveRealHooks()
  {
    InputDispatch dispatcher = InputDispatch.Create(() => -1);
    Assert.False(dispatcher.IsDoubleBacked);
    Assert.Equal(NativeInputChord(), dispatcher.ChordHook);
    Assert.Equal(NativeInputText(), dispatcher.TextHook);
    Assert.Equal(NativeInputDrag(), dispatcher.DragHook);
    Assert.Equal(NativeInputButtonAt(), dispatcher.ButtonAtHook);
    Assert.Equal(NativeInputWheelAt(), dispatcher.WheelAtHook);
  }

  // Static method-group probes: delegates over the same static method compare equal,
  // so these pin the hook TARGETS without dispatching real input.
  private static Func<KeyChord, bool> NativeInputChord() => NativeInput.SendChord;
  private static Func<string, bool> NativeInputText() => NativeInput.SendText;
  private static Func<string, int, int, int, int, bool> NativeInputDrag() => NativeInput.SendDrag;
  private static Func<string, int, int, bool> NativeInputButtonAt() => NativeInput.SendMouseButtonAt;
  private static Func<string, int, int, int, bool> NativeInputWheelAt() => NativeInput.SendWheelAt;
}
