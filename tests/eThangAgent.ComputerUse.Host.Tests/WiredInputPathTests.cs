using System.Text.Json;

namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>Fix-round C1/I2: the REAL wired input path (HandleInputMethod → InputRouter →
///     Dispatch) must perform real dispatch and honor the strategy=event foreground gate
///     with a real (abstracted) foreground read. Fake success is forbidden.</summary>
public class WiredInputPathTests
{
  [Fact]
  public void PressKey_OnWiredPath_PerformsRealDispatch()
  {
    PipeServer server = FakeConnectionFactory.Authorized(1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "press_key", JsonDocument.Parse("""{"key":"a"}""").RootElement, connectionId: 1);
    // Live-desktop honesty: the REAL SendInput either lands (accepted) or is refused by the
    // injection gate (internal, action_sent=false). Both are honest; fake success is not.
    Assert.True(
      reply.Error is null || (reply.Error.Value.Code == "internal" && reply.Error.Value.Details == "action_sent=false"),
      "expected accepted or an honest internal failure, got: " + (reply.Error?.Code ?? "ok"));
    // Real dispatch: the native SendInput path must have been exercised.
    Assert.True(server.InputDispatch.SendInputCalls > 0, "dispatch must call SendInput before reporting accepted");
  }

  [Fact]
  public void StrategyEvent_OnWiredPath_GateRefuses_WithRealForeground()
  {
    PipeServer server = FakeConnectionFactory.WithForeground(expected: 42, actual: 99);
    _ = server.Dispatch(0, "authenticate", JsonDocument.Parse("""{"token":"t"}""").RootElement, connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "click", JsonDocument.Parse("""{"strategy":"event","app_ref":{"pid":42}}""").RootElement, connectionId: 1);
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("foreground_required", reply.Error.Value.Code);
    Assert.Equal(0, server.InputDispatch.SendInputCalls);
  }
};
