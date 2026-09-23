using System.Text.Json;

namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>Fix round C1/I2: the REAL wired input path (HandleInputMethod → Dispatch)
///     performs real dispatch; the strategy=event foreground gate refuses with
///     foreground_required and NOTHING dispatched (counted probe stays at zero).</summary>
public class InputDispatchForegroundGateTests
{
  [Fact]
  public void StrategyEvent_ForegroundMatches_Dispatches()
  {
    PipeServer server = FakeConnectionFactory.WithRecordingClick(foreground: 42);
    _ = server.Dispatch(0, "authenticate", JsonDocument.Parse("""{"token":"t"}""").RootElement, connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "click", JsonDocument.Parse("""{"strategy":"event","x":10,"y":10,"app_ref":{"pid":42}}""").RootElement, connectionId: 1);
    // The send sink is a RECORDING double (no real input is dispatched); the targeted
    // click routes through the positioned-button hook, so the counter proves the
    // dispatch was ATTEMPTED and the receipt is accepted.
    Assert.Null(reply.Error);
    Assert.True(server.InputDispatch.SendInputCalls > 0, "real dispatch path must have run");
    _ = Assert.NotNull(reply.Result);
  }

  [Fact]
  public void StrategyEvent_ForegroundMismatch_IsForegroundRequired_NothingDispatched()
  {
    PipeServer server = FakeConnectionFactory.WithForeground(expected: 42, actual: 99);
    _ = server.Dispatch(0, "authenticate", JsonDocument.Parse("""{"token":"t"}""").RootElement, connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "click", JsonDocument.Parse("""{"strategy":"event","x":10,"y":10,"app_ref":{"pid":42}}""").RootElement, connectionId: 1);
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("foreground_required", reply.Error.Value.Code);
    Assert.Equal(0, server.InputDispatch.SendInputCalls);
  }
};
