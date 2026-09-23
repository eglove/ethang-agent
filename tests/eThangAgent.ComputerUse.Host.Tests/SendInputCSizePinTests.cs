using System.Reflection;
using System.Runtime.InteropServices;

namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>Fix round 4 (controller ruling, layout-pin family): the cbSize argument
///     SendInput receives must be Marshal.SizeOf(Input) - the struct-level offset pins
///     cannot see it, because cbSize lives in the CALL, not in the type. The pin
///     reflects over NativeInput.Inject's IL: the call must pass Marshal.SizeOf (generic over T)
///     constructed over the SAME Input type the P/Invoke array carries. A hardcoded
///     count (the accepted-1-of-1 garbage-shape bug class) fails here.</summary>
public class SendInputCSizePinTests
{
  [Fact]
  public void Inject_ComputesCbSize_ViaMarshalSizeOfInput_ForTheRealSendInputCall()
  {
    MethodInfo inject = typeof(NativeInput).GetMethod("Inject", BindingFlags.NonPublic | BindingFlags.Static)
      ?? throw new InvalidOperationException("NativeInput.Inject disappeared");
    MethodInfo sendInput = typeof(NativeInput).GetMethod("SendInput", BindingFlags.NonPublic | BindingFlags.Static)
      ?? throw new InvalidOperationException("NativeInput.SendInput disappeared");

    int sizeOfInputCalls = 0;
    int sendInputCalls = 0;
    byte[] il = inject.GetMethodBody()!.GetILAsByteArray()!;
    for (int i = 0; i < il.Length; i++)
    {
      if ((il[i] == 0x28 || il[i] == 0x6F) && i + 4 < il.Length)
      {
        int token = BitConverter.ToInt32(il, i + 1);
        MethodBase? callee = Resolve(inject.Module, token);
        if (callee is null)
        {
          continue;
        }

        if (callee is MethodInfo cm && cm.DeclaringType == typeof(Marshal) && cm.Name == "SizeOf"
          && cm.IsGenericMethod && cm.GetGenericArguments().Length == 1
          && cm.GetGenericArguments()[0] == typeof(NativeInput.Input))
        {
          sizeOfInputCalls++;
        }

        if (callee == sendInput)
        {
          sendInputCalls++;
        }
      }
    }

    Assert.True(sendInputCalls == 1, "Inject must call SendInput exactly once");
    Assert.True(sizeOfInputCalls == 1, "cbSize must be computed via Marshal.SizeOf<Input>, never hardcoded");
  }

  [Fact]
  public void MarshalSizeOfInput_Is40_TheWin64InputSizeTheOffsetsAssume() =>
    // The wire contract the whole pin family defends: cbSize == 40 == sizeof(INPUT),
    // so the system parses every field at the pinned offsets.
    Assert.Equal(40, Marshal.SizeOf<NativeInput.Input>());

  private static MethodBase? Resolve(Module module, int token)
  {
#pragma warning disable IDE0022 // Named decision: try/catch cannot be an expression body; the suppression silences the false offer.
    try
    {
      return module.ResolveMethod(token);
    }
    catch (ArgumentException)
    {
      return null;
    }
#pragma warning restore IDE0022
  }
}
