using System.Text.Json;

namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>InputDispatch strategy=event foreground gate (task 17): when the caller
///     pins strategy=event, the broker requires GetForegroundWindow to match the
///     target app/window; on mismatch NOTHING is dispatched (foreground_required).
///     The Win32 foreground call is abstracted, so no live desktop is needed.</summary>
public class InputDispatchForegroundGateTests
{
  [Fact]
  public void StrategyEvent_ForegroundMatches_DispatchesAccepted()
  {
    InputDispatch dispatch = InputDispatch.ForTesting(foregroundPid: 42, () => 42);
    using InputOperation operation = CreateOperation();
    BrokerResponse response = dispatch.Click(operation, JsonDocument.Parse("""{"strategy":"event","app_ref":{"pid":42},"mouse_button":"left","click_count":1}""").RootElement, targetPid: 42);
    Assert.Null(response.Error);
    _ = Assert.NotNull(response.Result);
  }

  [Fact]
  public void StrategyEvent_ForegroundMismatch_IsForegroundRequired_NothingDispatched()
  {
    InputDispatch dispatch = InputDispatch.ForTesting(foregroundPid: 42, () => 99);
    using InputOperation operation = CreateOperation();
    JsonElement parameters = JsonDocument.Parse("""{"strategy":"event","app_ref":{"pid":42}}""").RootElement;
    BrokerResponse response = dispatch.Click(operation, parameters, targetPid: 42);
    _ = Assert.NotNull(response.Error);
    Assert.Equal("foreground_required", response.Error.Value.Code);
  }

  [Fact]
  public void StrategyEvent_Mismatch_SentNoInput()
  {
    // Delivery-state honesty: a refused dispatch must not have touched the hardware.
    InputDispatch dispatch = InputDispatch.ForTesting(foregroundPid: 42, () => 7);
    using InputOperation operation = CreateOperation();
    JsonElement parameters = JsonDocument.Parse("""{"strategy":"event"}""").RootElement;
    BrokerResponse response = dispatch.Click(operation, parameters, targetPid: 42);
    Assert.Equal(0, dispatch.SendInputCalls);
    _ = Assert.NotNull(response.Error);
  }

  [Fact]
  public void NoStrategy_Auto_DoesNotGateOnForeground()
  {
    InputDispatch dispatch = InputDispatch.ForTesting(foregroundPid: 42, () => 7);
    using InputOperation operation = CreateOperation();
    JsonElement parameters = JsonDocument.Parse("""{"app_ref":{"pid":42}}""").RootElement;
    _ = dispatch.Click(operation, parameters, targetPid: 42);
    Assert.Equal(1, dispatch.SendInputCalls);
  }

  private static InputOperation CreateOperation()
  {
    InputSerializer serializer = new();
    return serializer.TryBegin("click")!;
  }
};
