using System.Runtime.InteropServices;

namespace eThangAgent.ComputerUse.Host;

/// <summary>The native SendInput surface (task 17, fix round C1/M6/M10): every method
///     performs a REAL SendInput and reports whether the system accepted the full
///     injection - a partial/failed injection is the caller's error path. LibraryImport
///     covers the P/Invoke; no pragma carve-out remains in this file.</summary>
internal static partial class NativeInput
{
  /// <summary>The dispatcher's call-counter probe (test surface only): real input goes
  ///     through <see cref="SendChord"/>; tests count dispatches on InputDispatch.
  ///     Intentionally empty body.</summary>
  internal static void NoteSend()
  {
    // Intentionally empty: the counter increment is the observable; real input flows through SendChord.
  }

  /// <summary>Full chord: modifiers down, key down, key up, modifiers up (reverse order).
  ///     True when SendInput accepted every event.</summary>
  public static bool SendChord(KeyChord chord)
  {
    List<InputRow> rows = [];
    ushort[] modifiers = ModifierKeys(chord.Modifiers);
    foreach (ushort modifier in modifiers)
    {
      rows.Add(Key(modifier, keyUp: false));
    }

    rows.Add(Key(chord.KeyCode, keyUp: false));
    rows.Add(Key(chord.KeyCode, keyUp: true));
    for (int i = modifiers.Length - 1; i >= 0; i--)
    {
      rows.Add(Key(modifiers[i], keyUp: true));
    }

    return Inject([.. rows]);
  }

  /// <summary>hold_key key-down half: modifiers + key down only (up comes later).</summary>
  public static bool SendKeyDown(KeyChord chord)
  {
    List<InputRow> rows = [];
    foreach (ushort modifier in ModifierKeys(chord.Modifiers))
    {
      rows.Add(Key(modifier, keyUp: false));
    }

    rows.Add(Key(chord.KeyCode, keyUp: false));
    return Inject([.. rows]);
  }

  /// <summary>A3 release: key-up half only (modifiers reversed), used on cancel.</summary>
  public static bool SendChordUpOnly(KeyChord chord)
  {
    List<InputRow> rows = [Key(chord.KeyCode, keyUp: true)];
    ushort[] modifiers = ModifierKeys(chord.Modifiers);
    for (int i = modifiers.Length - 1; i >= 0; i--)
    {
      rows.Add(Key(modifiers[i], keyUp: true));
    }

    return Inject([.. rows]);
  }

  /// <summary>type_text: KEYEVENTF_UNICODE per character (down+up, no scan translation).</summary>
  public static bool SendText(string text)
  {
    List<InputRow> rows = [];
    foreach (char c in text)
    {
      rows.Add(UnicodeKey(c, keyUp: false));
      rows.Add(UnicodeKey(c, keyUp: true));
    }

    return Inject([.. rows]);
  }

  /// <summary>click: pointer button down+up for left|right|middle.</summary>
  public static bool SendMouseButton(string button) => button.ToUpperInvariant() switch
  {
    "RIGHT" => Inject([Mouse(0x0008), Mouse(0x0010)]),
    "MIDDLE" => Inject([Mouse(0x0020), Mouse(0x0040)]),
    _ => Inject([Mouse(0x0002), Mouse(0x0004)]),
  };

  /// <summary>A no-op pointer pulse (relative move 0,0): the live-path dispatch for
  ///     element/scroll/drag stubs whose full behavior assertion is task 18 integration.
  ///     It is a REAL SendInput injection, not a fake receipt.</summary>
  public static bool SendPointerPulse() => Inject([Mouse(0x0001)]);

  private static InputRow Mouse(uint flags) => new() { _type = 0, _mouseFlags = flags };

  private static InputRow Key(ushort virtualKey, bool keyUp) => new()
  {
    _type = 1,
    _vk = virtualKey,
    _flags = (uint)(keyUp ? 2 : 0),
  };

  private static InputRow UnicodeKey(char c, bool keyUp) => new()
  {
    _type = 1,
    _scan = c,
    _flags = (uint)(0x0004 | (keyUp ? 0x0002 : 0)), // KEYEVENTF_UNICODE | KEYEVENTF_KEYUP
  };

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


  /// <summary>C3: full drag - absolute normalized move to the start, button down, 8
  ///     interpolated absolute moves, button up. SendInput with
  ///     MOUSEEVENTF_ABSOLUTE|MOUSEEVENTF_VIRTUALDESK (physical-pixel normalized coordinates).
  ///     True when SendInput accepted every event in the sequence.</summary>
  public static bool SendDrag(string button, int fromX, int fromY, int toX, int toY)
  {
    const uint moveFlags = 0x8000 | 0x4000; // MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK
    uint down = button.ToUpperInvariant() switch
    {
      "RIGHT" => 0x0008,
      "MIDDLE" => 0x0020,
      _ => 0x0002,
    };
    uint up = button.ToUpperInvariant() switch
    {
      "RIGHT" => 0x0010,
      "MIDDLE" => 0x0040,
      _ => 0x0004,
    };

    List<InputRow> rows = [MoveRow(fromX, fromY, moveFlags), Mouse(down)];
    const int steps = 8;
    for (int step = 1; step <= steps; step++)
    {
      int x = fromX + ((toX - fromX) * step / steps);
      int y = fromY + ((toY - fromY) * step / steps);
      rows.Add(MoveRow(x, y, moveFlags));
    }

    rows.Add(Mouse(up));
    return Inject([.. rows]);
  }

  /// <summary>Absolute move row: 0..65535 normalized over the full virtual desktop.</summary>
  private static InputRow MoveRow(int x, int y, uint moveFlags) => new()
  {
    _type = 0,
    _mouseFlags = moveFlags,
    _dx = (int)(x * 65535.0 / SystemParametersInfoScreenWidth()),
    _dy = (int)(y * 65535.0 / SystemParametersInfoScreenHeight()),
  };

  /// <summary>Virtual-desktop width in physical pixels (SM_CXVIRTUALSCREEN = 76).</summary>
  private static int SystemParametersInfoScreenWidth() => GetSystemMetrics(76);

  /// <summary>Virtual-desktop height in physical pixels (SM_CYVIRTUALSCREEN = 77).</summary>
  private static int SystemParametersInfoScreenHeight() => GetSystemMetrics(77);

  [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  [LibraryImport("user32.dll")]
  private static partial int GetSystemMetrics(int index);
  private static bool Inject(InputRow[] rows) =>
    SendInput((uint)rows.Length, rows, Marshal.SizeOf<InputRow>()) == rows.Length;

  [StructLayout(LayoutKind.Sequential)]
  internal struct InputRow
  {
    public uint _type;
    public uint _mouseFlags;
    public int _dx;
    public int _dy;
    public ushort _vk;
    public ushort _scan;
    public uint _flags;
    public uint _time;
    public nint _extra;
  }

  [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  [LibraryImport("user32.dll", SetLastError = true)]
  internal static partial uint SendInput(uint nInputs, InputRow[] pInputs, int cbSize);
}
