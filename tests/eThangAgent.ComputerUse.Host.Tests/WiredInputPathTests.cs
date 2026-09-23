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
    // Hazard-rule fix: wire tests use recording doubles - the ROUTE is real
    // (HandleInputMethod -> Dispatch -> send hook), never a real chord.
    PipeServer server = FakeConnectionFactory.WithAlwaysSucceedingInput();
    _ = server.Dispatch(0, "authenticate", JsonDocument.Parse("""{"token":"t"}""").RootElement, connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "press_key", JsonDocument.Parse("""{"key":"a"}""").RootElement, connectionId: 1);
    Assert.Null(reply.Error); // the recording double succeeds: the receipt is accepted
    Assert.True(server.InputDispatch.SendInputCalls > 0, "dispatch must reach the send hook before reporting accepted");
    Assert.True(server.InputDispatch.IsDoubleBacked, "the wire test must run on the double-backed dispatcher, never the real SendInput path");
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
  [Fact]
  public void PressKey_MissingParamsKey_TypedInvalidRequest_NotCrash()
  {
    PipeServer server = FakeConnectionFactory.Authorized(1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "press_key", null, connectionId: 1);
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("invalid_request", reply.Error.Value.Code);
    Assert.Contains("key", reply.Error.Value.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void HoldKey_MissingParamsKey_TypedInvalidRequest_NotCrash()
  {
    PipeServer server = FakeConnectionFactory.Authorized(1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "hold_key", null, connectionId: 1);
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("invalid_request", reply.Error.Value.Code);
  }
};
