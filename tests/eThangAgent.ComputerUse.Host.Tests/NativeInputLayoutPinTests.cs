using System.Runtime.InteropServices;

namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>Fix round 4 (layout regression pins): the committed InputRow laid every row
///     out sequentially (type first), so the system read MOUSEINPUT.dwFlags at INPUT offset
///     4 (Win32 truth: 20) and KEYBDINPUT.wVk at offset 16 (truth: 8) - mouse rows were
///     inert and keyboard rows were garbage, yet SendInput still reported the accepted
///     count, so every path reported action_sent=true dishonestly. These pins make that
///     bug class visible: any future layout drift fails fast here, before a single input
///     event is dispatched with a wrong shape.</summary>
public class NativeInputLayoutPinTests
{
  [Fact]
  public void Input_Type_SitsAtInputOffsetZero_ViaUnionOverlay() => Assert.Equal(0, Marshal.OffsetOf<NativeInput.Input>(nameof(NativeInput.Input._type)).ToInt64());

  [Fact]
  public void Input_Keyboard_wVk_SitsAtInputOffset8_And_Mouse_dwFlags_At20()
  {
    long keyboardAt = Marshal.OffsetOf<NativeInput.Input>(nameof(NativeInput.Input._keyboard)).ToInt64();
    long wVkAt = keyboardAt + Marshal.OffsetOf<NativeInput.Input.KeyboardRow>(nameof(NativeInput.Input.KeyboardRow._wVk)).ToInt64();
    Assert.Equal(8, wVkAt);

    long mouseAt = Marshal.OffsetOf<NativeInput.Input>(nameof(NativeInput.Input._mouse)).ToInt64();
    long dwFlagsAt = mouseAt + Marshal.OffsetOf<NativeInput.Input.MouseRow>(nameof(NativeInput.Input.MouseRow._dwFlags)).ToInt64();
    Assert.Equal(20, dwFlagsAt);
  }

  [Fact]
  public void Input_MouseRow_CarriesTheFullMouseInputShape_IncludingMouseData()
  {
    Assert.Equal(0, Marshal.OffsetOf<NativeInput.Input.MouseRow>(nameof(NativeInput.Input.MouseRow._dx)).ToInt64());
    Assert.Equal(4, Marshal.OffsetOf<NativeInput.Input.MouseRow>(nameof(NativeInput.Input.MouseRow._dy)).ToInt64());
    Assert.Equal(8, Marshal.OffsetOf<NativeInput.Input.MouseRow>(nameof(NativeInput.Input.MouseRow._mouseData)).ToInt64());
    Assert.Equal(12, Marshal.OffsetOf<NativeInput.Input.MouseRow>(nameof(NativeInput.Input.MouseRow._dwFlags)).ToInt64());
    Assert.Equal(16, Marshal.OffsetOf<NativeInput.Input.MouseRow>(nameof(NativeInput.Input.MouseRow._time)).ToInt64());
    Assert.Equal(24, Marshal.OffsetOf<NativeInput.Input.MouseRow>(nameof(NativeInput.Input.MouseRow._dwExtraInfo)).ToInt64());
    Assert.Equal(32, Marshal.SizeOf<NativeInput.Input.MouseRow>());
  }

  [Fact]
  public void Input_KeyboardRow_CarriesTheKeybdInputShape() => Assert.Equal(24, Marshal.SizeOf<NativeInput.Input.KeyboardRow>());

  [Fact]
  public void Input_Is40Bytes_PerTheWin64InputRules() => Assert.Equal(40, Marshal.SizeOf<NativeInput.Input>());

  [Fact]
  public void SendInput_TravelsTheRealPInvoke_WithZeroRowsAndForeignCbSize()
  {
    // Zero rows travel the real P/Invoke path with a deliberately-wrong cbSize: the
    // system accepts nothing (count 0) and sends no input. This keeps the P/Invoke
    // signature struct-typed and callable; the offset pins above catch shape drift.
    uint accepted = NativeInput.SendInput(0, [], 999);
    Assert.Equal(0u, accepted);
  }
}
