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
    PipeServer server = FakeConnectionFactory.WithForeground(expected: 42, actual: 42);
    _ = server.Dispatch(0, "authenticate", JsonDocument.Parse("""{"token":"t"}""").RootElement, connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "click", JsonDocument.Parse("""{"strategy":"event","app_ref":{"pid":42}}""").RootElement, connectionId: 1);
    // The real SendInput either lands (accepted) or is honestly refused in the test host
    // (internal, action_sent=false); the counter proves the dispatch was ATTEMPTED.
    Assert.True(server.InputDispatch.SendInputCalls > 0, "real dispatch must have been attempted");
    if (reply.Error is null)
    {
      _ = Assert.NotNull(reply.Result);
    }
    else
    {
      Assert.Equal("internal", reply.Error.Value.Code);
      Assert.Equal("action_sent=false", reply.Error.Value.Details);
    }
  }

  [Fact]
  public void StrategyEvent_ForegroundMismatch_IsForegroundRequired_NothingDispatched()
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
