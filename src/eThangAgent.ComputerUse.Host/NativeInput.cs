using System.Runtime.InteropServices;

namespace eThangAgent.ComputerUse.Host;

/// <summary>The native SendInput surface (task 17): chords and raw key sequences.
///     Kept separate from the gate logic so the dispatcher stays unit-testable.</summary>
internal static class NativeInput
{
  /// <summary>Probe hook for the dispatcher's call counter (test surface only).</summary>
  internal static void NoteSend()
  {
    // Intentionally empty: the counter is the observable; real input goes through SendChord.
  }

  /// <summary>Sends one normalized chord (modifiers down, key down, key up, modifiers
  ///     up). Returns false when SendInput reports a partial/failed injection.</summary>
  public static bool SendChord(KeyChord chord)
  {
    InputRow[] inputs = BuildChord(chord);
    return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<InputRow>()) == inputs.Length;
  }

  internal static InputRow[] BuildChord(KeyChord chord)
  {
    List<InputRow> rows = [];
    foreach (ushort modifierKey in ModifierKeys(chord.Modifiers))
    {
      rows.Add(Key(modifierKey, keyUp: false));
    }

    rows.Add(Key(chord.KeyCode, keyUp: false));
    rows.Add(Key(chord.KeyCode, keyUp: true));
    for (int i = ModifierKeys(chord.Modifiers).Length - 1; i >= 0; i--)
    {
      rows.Add(Key(ModifierKeys(chord.Modifiers)[i], keyUp: true));
    }

    return [.. rows];
  }

  private static ushort[] ModifierKeys(KeyModifiers modifiers)
  {
    List<ushort> keys = [];
    if (modifiers.HasFlag(KeyModifiers.Control))
    {
      keys.Add(0x11);
    }

    if (modifiers.HasFlag(KeyModifiers.Alt))
    {
      keys.Add(0x12);
    }

    if (modifiers.HasFlag(KeyModifiers.Shift))
    {
      keys.Add(0x10);
    }

    if (modifiers.HasFlag(KeyModifiers.Win))
    {
      keys.Add(0x5B);
    }

    return [.. keys];
  }

  private static InputRow Key(ushort virtualKey, bool keyUp) => new()
  {
    _type = 1,
    _vk = virtualKey,
    _flags = (uint)(keyUp ? 2 : 0),
  };

  [StructLayout(LayoutKind.Sequential)]
  internal struct InputRow
  {
    public uint _type;
    public ushort _vk;
    public ushort _scan;
    public uint _flags;
    public uint _time;
    public nint _extra;
  }

  [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
#pragma warning disable SYSLIB1054 // Named decision (T12-13 precedent): LibraryImport needs unsafe blocks; blittable layout here marshals identically.
  [DllImport("user32.dll", SetLastError = true)]
  private static extern uint SendInput(uint nInputs, InputRow[] pInputs, int cbSize);
#pragma warning restore SYSLIB1054
}
