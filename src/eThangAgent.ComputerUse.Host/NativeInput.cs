using System.Runtime.InteropServices;

namespace eThangAgent.ComputerUse.Host;

/// <summary>The native SendInput surface (task 17, fix rounds C1/M6/M10, layout rebuilt in
///     fix round 4): every method performs a REAL SendInput and reports whether the system
///     accepted the full injection - a partial/failed injection is the caller's error path.
///     The wire struct is the Win32 INPUT union: an explicit layout overlaying MOUSEINPUT
///     and KEYBDINPUT at offset 0 with the type tag at offset 4, so keyboard wVk sits at
///     INPUT offset 8 and mouse dwFlags at INPUT offset 20 (pinned by NativeInputLayoutPinTests;
///     the previous shared sequential row read both fields from the wrong offsets). LibraryImport
///     covers the P/Invoke.</summary>
internal static partial class NativeInput
{
  private const uint MouseMove = 0x0001;
  private const uint MouseAbsolute = 0x8000;
  private const uint MouseVirtualDesk = 0x4000;
  private const int ExtentWidthMetric = 78;  // SM_CXVIRTUALSCREEN - the EXTENT, not the origin (76)
  private const int ExtentHeightMetric = 79; // SM_CYVIRTUALSCREEN - the EXTENT, not the origin (77)

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
    List<Input> rows = [];
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
    List<Input> rows = [];
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
    List<Input> rows = [Key(chord.KeyCode, keyUp: true)];
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
    List<Input> rows = [];
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
  public static bool SendPointerPulse() => Inject([Mouse(MouseMove)]);

  private static Input Mouse(uint flags) => new() { _type = 0, _mouse = new Input.MouseRow { _dwFlags = flags } };

  private static Input Key(ushort virtualKey, bool keyUp) => new()
  {
    _type = 1,
    _keyboard = new Input.KeyboardRow
    {
      _wVk = virtualKey,
      _dwFlags = (uint)(keyUp ? 2 : 0),
    },
  };

  private static Input UnicodeKey(char c, bool keyUp) => new()
  {
    _type = 1,
    _keyboard = new Input.KeyboardRow
    {
      _wScan = c,
      _dwFlags = (uint)(0x0004 | (keyUp ? 0x0002 : 0)), // KEYEVENTF_UNICODE | KEYEVENTF_KEYUP
    },
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


  /// <summary>C3/fix round 4: full drag - absolute normalized move to the start (with
  ///     MOUSEEVENTF_MOVE: ABSOLUTE|VIRTUALDESK alone is a no-op event), button down, 8
  ///     interpolated absolute moves, button up. Coordinates normalize against the
  ///     virtual-desktop EXTENTS (SM_CX/CYVIRTUALSCREEN); zero extents refuse the send
  ///     honestly instead of dividing by zero. True when SendInput accepted every event.</summary>
  public static bool SendDrag(string button, int fromX, int fromY, int toX, int toY)
  {
    if (!TryExtents(out int cx, out int cy))
    {
      return false;
    }

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

    List<Input> rows = [MoveRow(fromX, fromY, cx, cy), Mouse(down)];
    const int steps = 8;
    for (int step = 1; step <= steps; step++)
    {
      (int x, int y) = StepPoint(fromX, fromY, toX, toY, steps, step);
      rows.Add(MoveRow(x, y, cx, cy));
    }

    rows.Add(Mouse(up));
    return Inject([.. rows]);
  }

  /// <summary>Linear interpolation between the drag endpoints, both ends inclusive.</summary>
  internal static (int X, int Y) StepPoint(int fromX, int fromY, int toX, int toY, int steps, int step)
  {
    int x = fromX + ((toX - fromX) * step / steps);
    int y = fromY + ((toY - fromY) * step / steps);
    return (x, y);
  }

  /// <summary>Absolute move row parts: 0..65535 normalized over the virtual-desktop extents,
  ///     clamped into the normalized range; flags carry MOVE|ABSOLUTE|VIRTUALDESK.</summary>
  internal static (uint flags, int dx, int dy) MoveRowParts(int x, int y, int cx, int cy)
  {
    double dx = Math.Clamp(x * 65535.0 / cx, 0, 65535);
    double dy = Math.Clamp(y * 65535.0 / cy, 0, 65535);
    const uint moveFlags = MouseMove | MouseAbsolute | MouseVirtualDesk;
    return (moveFlags, (int)Math.Round(dx), (int)Math.Round(dy));
  }

  private static Input MoveRow(int x, int y, int cx, int cy)
  {
    (uint flags, int dx, int dy) = MoveRowParts(x, y, cx, cy);
    return new Input { _type = 0, _mouse = new Input.MouseRow { _dwFlags = flags, _dx = dx, _dy = dy } };
  }

  /// <summary>Virtual-desktop extents (physical pixels); false when either is not positive
  ///     - the honest refusal for a degenerate desktop, never a divide-by-zero.</summary>
  private static bool TryExtents(out int cx, out int cy)
  {
    cx = GetSystemMetrics(ExtentWidthMetric);
    cy = GetSystemMetrics(ExtentHeightMetric);
    return cx > 0 && cy > 0;
  }

  [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  [LibraryImport("user32.dll")]
  private static partial int GetSystemMetrics(int index);

  private static bool Inject(Input[] rows) =>
    SendInput((uint)rows.Length, rows, Marshal.SizeOf<Input>()) == rows.Length;

  /// <summary>The Win32 INPUT union (explicit layout, Win64 shapes): the type tag rides
  ///     offset 0 (padded to 8) and MOUSEINPUT/KEYBDINPUT overlay at offset 8 - exactly the
  ///     C compiler's layout. Keyboard wVk lands at INPUT offset 8 and mouse dwFlags at
  ///     INPUT offset 20 (dx 4 + dy 4 + mouseData 4 precede it); the 32-byte MOUSEINPUT
  ///     member grows the struct to 40. Pinned by NativeInputLayoutPinTests.</summary>
  [StructLayout(LayoutKind.Explicit, Pack = 8, Size = 40)]
  internal struct Input
  {
    [FieldOffset(0)]
    public uint _type;

    [FieldOffset(8)]
    public MouseRow _mouse;

    [FieldOffset(8)]
    public KeyboardRow _keyboard;

    /// <summary>Win32 MOUSEINPUT (union-relative): dx@0 dy@4 mouseData@8 dwFlags@12 time@16 dwExtraInfo@24.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MouseRow
    {
      public int _dx;
      public int _dy;
      public uint _mouseData;
      public uint _dwFlags;
      public uint _time;
      public nint _dwExtraInfo;
    }

    /// <summary>Win32 KEYBDINPUT (union-relative): wVk@0 wScan@2 dwFlags@4 time@8 dwExtraInfo@16.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct KeyboardRow
    {
      public ushort _wVk;
      public ushort _wScan;
      public uint _dwFlags;
      public uint _time;
      public nint _dwExtraInfo;
    }
  }

  [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  [LibraryImport("user32.dll", SetLastError = true)]
  internal static partial uint SendInput(uint nInputs, Input[] pInputs, int cbSize);
}
