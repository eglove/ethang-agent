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
    _ = server.Dispatch(0, "authenticate", JsonElement.Parse("""{"token":"t"}"""), connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "click", JsonElement.Parse("""{"strategy":"event","x":10,"y":10,"app_ref":{"pid":42}}"""), connectionId: 1);
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
    _ = server.Dispatch(0, "authenticate", JsonElement.Parse("""{"token":"t"}"""), connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "click", JsonElement.Parse("""{"strategy":"event","x":10,"y":10,"app_ref":{"pid":42}}"""), connectionId: 1);
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("foreground_required", reply.Error.Value.Code);
    Assert.Equal(0, server.InputDispatch.SendInputCalls);
  }

  [Fact]
  public void Key_StrategyEvent_ForegroundMismatch_IsForegroundRequired_NothingDispatched()
  {
    // F8 gate closure: strategy=event routes press_key through the same foreground gate
    // as click/scroll/drag - a background event chord would type into whatever window
    // currently has focus.
    PipeServer server = FakeConnectionFactory.WithForeground(expected: 42, actual: 99);
    _ = server.Dispatch(0, "authenticate", JsonElement.Parse("""{"token":"t"}"""), connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "press_key", JsonElement.Parse("""{"key":"x","strategy":"event","app_ref":{"pid":42}}"""), connectionId: 1);
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("foreground_required", reply.Error.Value.Code);
    Assert.Equal(0, server.InputDispatch.SendInputCalls);
  }

  [Fact]
  public void Key_StrategyEvent_ForegroundMatches_Dispatches()
  {
    PipeServer server = FakeConnectionFactory.WithRecordingClick(foreground: 42);
    _ = server.Dispatch(0, "authenticate", JsonElement.Parse("""{"token":"t"}"""), connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "press_key", JsonElement.Parse("""{"key":"x","strategy":"event","app_ref":{"pid":42}}"""), connectionId: 1);
    Assert.Null(reply.Error);
    Assert.True(server.InputDispatch.SendInputCalls > 0, "the chord dispatch must have run");
  }

  [Fact]
  public void Key_NoStrategy_IsGatedToo_BackgroundTargetIsRefused()
  {
    // Injection reaches whatever window holds focus, so a strategy-less chord aimed at a
    // background window is the same hazard as a strategy=event one: it types into whatever
    // the user is looking at. The gate is therefore unconditional, and this pins it.
    // LIVE-FIRE FIX: this test used to run on a zero-hook server — PipeServer's
    // resolution contract resolves that to the REAL NativeInput — and its ungated
    // press_key live-fired a real 'y' keystroke into whatever window had focus on
    // the developer's desktop while the suite ran. The factory now wires recording
    // doubles; the assertion pins that invariant so a future factory change that
    // reintroduces the real path fails HERE instead of typing into someone's editor.
    PipeServer server = FakeConnectionFactory.WithForeground(expected: 42, actual: 99);
    Assert.True(server.InputDispatch.IsDoubleBacked, "the gate test must run on the double-backed dispatcher, never the real SendInput path");
    _ = server.Dispatch(0, "authenticate", JsonElement.Parse("""{"token":"t"}"""), connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "press_key", JsonElement.Parse("""{"key":"y","app_ref":{"pid":42}}"""), connectionId: 1);
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("foreground_required", reply.Error.Value.Code);
    Assert.Equal(0, server.InputDispatch.SendInputCalls);
  }

  [Fact]
  public void Type_NoStrategy_IsGatedToo_BackgroundTargetIsRefused()
  {
    PipeServer server = FakeConnectionFactory.WithForeground(expected: 42, actual: 99);
    _ = server.Dispatch(0, "authenticate", JsonElement.Parse("""{"token":"t"}"""), connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "type_text", JsonElement.Parse("""{"text":"hello","app_ref":{"pid":42}}"""), connectionId: 1);
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("foreground_required", reply.Error.Value.Code);
    Assert.Equal(0, server.InputDispatch.SendInputCalls);
  }

  [Fact]
  public void Click_NoStrategy_BackgroundTarget_IsRefusedBeforeAnyRow()
  {
    PipeServer server = FakeConnectionFactory.WithForeground(expected: 42, actual: 99);
    _ = server.Dispatch(0, "authenticate", JsonElement.Parse("""{"token":"t"}"""), connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "click", JsonElement.Parse("""{"x":10,"y":10,"app_ref":{"pid":42}}"""), connectionId: 1);
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("foreground_required", reply.Error.Value.Code);
    Assert.Equal(0, server.InputDispatch.ClickCalls);
  }

  [Fact]
  public void Click_NoAppRef_DispatchesAtTheCurrentFocus()
  {
    // No app_ref means no target to verify: the dispatch goes to whatever holds focus by
    // intent, which is the documented way to act on the foreground window.
    PipeServer server = FakeConnectionFactory.WithRecordingClick(foreground: 99);
    _ = server.Dispatch(0, "authenticate", JsonElement.Parse("""{"token":"t"}"""), connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "click", JsonElement.Parse("""{"x":10,"y":10}"""), connectionId: 1);
    Assert.Null(reply.Error);
    Assert.Equal(1, server.InputDispatch.ClickCalls);
  }

  [Fact]
  public void Key_ForegroundTrue_BringsTheTargetForward_ThenDispatches()
  {
    // foreground=true is the escape hatch: the broker surfaces the window and re-reads the
    // foreground before dispatching, so the keystrokes land where they were aimed.
    int foreground = 99;
    PipeServer server = FakeConnectionFactory.WithForegroundAndBringForward(() => foreground, _ => foreground = 42);
    _ = server.Dispatch(0, "authenticate", JsonElement.Parse("""{"token":"t"}"""), connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "press_key",
        JsonElement.Parse("""{"key":"Tab","foreground":true,"app_ref":{"pid":42}}"""), connectionId: 1);
    Assert.Null(reply.Error);
    Assert.Equal(42, foreground);
    Assert.True(server.InputDispatch.SendInputCalls > 0, "the chord dispatch must have run after the window came forward");
  }

  [Fact]
  public void Key_ForegroundTrue_WhenTheWindowWillNotSurface_Refuses()
  {
    PipeServer server = FakeConnectionFactory.WithForegroundAndBringForward(() => 99, _ => { });
    _ = server.Dispatch(0, "authenticate", JsonElement.Parse("""{"token":"t"}"""), connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "press_key",
        JsonElement.Parse("""{"key":"Tab","foreground":true,"app_ref":{"pid":42}}"""), connectionId: 1);
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("foreground_required", reply.Error.Value.Code);
    Assert.Equal(0, server.InputDispatch.SendInputCalls);
  }
};
