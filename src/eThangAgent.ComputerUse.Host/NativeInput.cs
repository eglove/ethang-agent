using System.Runtime.InteropServices;

namespace eThangAgent.ComputerUse.Host;

/// <summary>The native SendInput surface (task 17, fix rounds C1/M6/M10, layout rebuilt in
///     fix round 4): every method performs a REAL SendInput and reports whether the system
///     accepted the full injection - a partial/failed injection is the caller's error path.
///     The wire struct is the Win32 INPUT union: an explicit layout overlaying MOUSEINPUT
///     and KEYBDINPUT at offset 0 with the type tag at offset 4, so keyboard wVk sits at
///     INPUT offset 8 and mouse dwFlags at INPUT offset 20 (pinned by NativeInputLayoutPinTests;
///     the previous shared sequential row read both fields from the wrong offsets). LibraryImport
///     covers the P/Invoke. Fix round 5: targeted clicks build move+button rows and
///     wheel scrolls build move+wheel rows over the same injected-extents seam.</summary>
internal static partial class NativeInput
{
  /// <summary>The virtual desktop in physical pixels: its extent and the screen point
  ///     of its top-left corner. The origin is (0, 0) on a single-monitor desktop and
  ///     negative whenever a monitor sits above or left of the primary one, which is
  ///     exactly the case an extent-only normalization gets wrong.</summary>
  internal readonly record struct DesktopMetrics(int Cx, int Cy, int Ox = 0, int Oy = 0)
  {
    /// <summary>True when the extent can be divided by: a degenerate desktop yields no
    ///     rows rather than a NaN or a divide-by-zero.</summary>
    public bool IsUsable => Cx > 0 && Cy > 0;
  }

  private const uint MouseMove = 0x0001;
  private const uint MouseAbsolute = 0x8000;
  private const uint MouseVirtualDesk = 0x4000;
  private const uint MouseWheel = 0x0800;

  /// <summary>Windows' wheel delta: one NOTCH of the wheel is 120 raw units; scroll
  ///     pages are injected as signed multiples of it.</summary>
  internal const int WheelDelta = 120;
  internal const int ExtentWidthMetric = 78;  // SM_CXVIRTUALSCREEN - the EXTENT
  internal const int ExtentHeightMetric = 79; // SM_CYVIRTUALSCREEN - the EXTENT
  internal const int OriginXMetric = 76;      // SM_XVIRTUALSCREEN - the virtual-desktop LEFT edge (negative on a left-hand monitor)
  internal const int OriginYMetric = 77;      // SM_YVIRTUALSCREEN - the virtual-desktop TOP edge

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

  /// <summary>Fix round 5 (F1): a targeted click - the built move+button rows over the
  ///     REAL virtual-desktop extents, injected as one SendInput batch. The cursor is
  ///     positioned FIRST (a button row acts at the current cursor position). Degenerate
  ///     extents build no rows, and Inject of an empty batch returns false - the honest
  ///     refusal, never a degenerate event.</summary>
  public static bool SendMouseButtonAt(string button, int x, int y) =>
    TryExtents(out DesktopMetrics desktop)
      && Inject(FromTaggedRows(BuildClickRows(button, x, y, desktop)));

  /// <summary>Fix round 5 (F3): a targeted vertical wheel scroll - the move row to the
  ///     resolved point, then the wheel row(s). Horizontal directions are refused by the
  ///     dispatcher before reaching here.</summary>
  public static bool SendWheelAt(string direction, int pages, int x, int y)
  {
    if (!TryExtents(out DesktopMetrics desktop))
    {
      return false;
    }

    List<TaggedMouseRow> rows = [Tag(BuildMoveParts(x, y, desktop))];
    rows.AddRange(BuildWheelRows(direction, pages));
    return Inject(FromTaggedRows([.. rows]));
  }

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
  ///     interpolated absolute moves, button up. The row SHAPE is built by
  ///     <see cref="BuildDragRows(string, int, int, int, int, DesktopMetrics)"/> over the
  ///     metrics the caller passes - the injected-metrics seam the pins exercise with fake
  ///     values - so this method only fetches the real virtual-desktop metrics and injects
  ///     the built rows. Coordinates normalize against the virtual desktop's extent AND
  ///     origin; a degenerate desktop refuses the send honestly instead of dividing by
  ///     zero. True when SendInput accepted every event.</summary>
  public static bool SendDrag(string button, int fromX, int fromY, int toX, int toY) =>
    TryExtents(out DesktopMetrics desktop)
    && Inject(FromDragRows(BuildDragRows(button, fromX, fromY, toX, toY, desktop)));

  /// <summary>Linear interpolation between the drag endpoints, both ends inclusive.</summary>
  internal static (int X, int Y) StepPoint(int fromX, int fromY, int toX, int toY, int steps, int step)
  {
    int x = fromX + ((toX - fromX) * step / steps);
    int y = fromY + ((toY - fromY) * step / steps);
    return (x, y);
  }

  /// <summary>One row of the drag sequence in wire-independent terms: the INPUT type tag,
  ///     the event flags, and the absolute normalized coordinates (button rows carry 0,0).</summary>
  internal readonly record struct DragRow(uint Type, uint Flags, int Dx, int Dy);

  /// <summary>Absolute move-row parts over the GIVEN extents with the desktop origin
  ///     at (0, 0) - the primary-monitor case, and the injected seam the row-shape pins
  ///     drive with fake metrics.</summary>
  internal static (uint flags, int dx, int dy) BuildMoveParts(int x, int y, int cx, int cy) =>
    BuildMoveParts(x, y, new DesktopMetrics(cx, cy));

  /// <summary>The move row over an ORIGIN-AWARE virtual desktop (see
  ///     <see cref="DesktopMetrics"/>): the screen point is rebased onto the virtual
  ///     desktop before it is normalized, because ABSOLUTE|VIRTUALDESK normalizes
  ///     against the virtual screen and not against the primary monitor. A point
  ///     outside the desktop clamps to its nearest edge rather than wrapping. A
  ///     degenerate extent yields the refusal marker (0, 0, 0) - no NaN, no
  ///     divide-by-zero.</summary>
  internal static (uint flags, int dx, int dy) BuildMoveParts(int x, int y, DesktopMetrics desktop)
  {
    if (!desktop.IsUsable)
    {
      return (0, 0, 0);
    }

    double dx = Math.Clamp((x - desktop.Ox) * 65535.0 / desktop.Cx, 0, 65535);
    double dy = Math.Clamp((y - desktop.Oy) * 65535.0 / desktop.Cy, 0, 65535);
    const uint moveFlags = MouseMove | MouseAbsolute | MouseVirtualDesk;
    return (moveFlags, (int)Math.Round(dx), (int)Math.Round(dy));
  }

  /// <summary>The full drag row sequence over the GIVEN extents (fake metrics - the
  ///     injected seam), pinned by DragSequencePinTests: a leading from-move
  ///     (MOVE|ABSOLUTE|VIRTUALDESK - ABSOLUTE|VIRTUALDESK alone is a no-op event),
  ///     button down at that position, 8 interpolated moves whose last row IS the
  ///     to-point, button up. Zero or negative extents yield NO rows - the honest
  ///     refusal - never a degenerate event.</summary>
  internal static DragRow[] BuildDragRows(string button, int fromX, int fromY, int toX, int toY, int cx, int cy) =>
    BuildDragRows(button, fromX, fromY, toX, toY, new DesktopMetrics(cx, cy));

  /// <summary>The drag row sequence over an origin-aware virtual desktop (see
  ///     <see cref="BuildMoveParts(int, int, DesktopMetrics)"/>).</summary>
  internal static DragRow[] BuildDragRows(string button, int fromX, int fromY, int toX, int toY, DesktopMetrics desktop)
  {
    string normalized = button.ToUpperInvariant();
    uint down = normalized switch
    {
      "RIGHT" => 0x0008,
      "MIDDLE" => 0x0020,
      _ => 0x0002,
    };
    uint up = normalized switch
    {
      "RIGHT" => 0x0010,
      "MIDDLE" => 0x0040,
      _ => 0x0004,
    };

    if (!desktop.IsUsable)
    {
      return [];
    }

    // Order is the Win32 drag idiom: position the cursor at the start FIRST (a button
    // row acts at the CURRENT cursor position), press, trace the path, release.
    (uint fromFlags, int fromDx, int fromDy) = BuildMoveParts(fromX, fromY, desktop);
    List<DragRow> rows = [new(0, fromFlags, fromDx, fromDy), new(0, down, 0, 0)];
    const int steps = 8;
    for (int step = 1; step <= steps; step++)
    {
      (int x, int y) = StepPoint(fromX, fromY, toX, toY, steps, step);
      (uint flags, int dx, int dy) = BuildMoveParts(x, y, desktop);
      rows.Add(new DragRow(0, flags, dx, dy));
    }

    rows.Add(new DragRow(0, up, 0, 0));
    return [.. rows];
  }

  /// <summary>One row of the click/wheel sequences in wire-independent terms: the INPUT
  ///     type tag, the event flags, the absolute normalized coordinates, the wheel
  ///     mouseData, and a test-facing tag naming the row's role.</summary>
  public readonly record struct TaggedMouseRow(uint Type, uint Flags, int Dx, int Dy, uint MouseData, string Role);

  /// <summary>A wheel row from direction + pages: MOUSEEVENTF_WHEEL with mouseData =
  ///     signed pages * WHEEL_DELTA (up is positive, down is negative). Horizontal
  ///     directions never reach this builder.</summary>
  internal static TaggedMouseRow BuildWheelRow(string direction, int pages)
  {
    return direction switch
    {
      _ when string.Equals(direction, "up", StringComparison.OrdinalIgnoreCase) =>
        new TaggedMouseRow(0, MouseWheel, 0, 0, (uint)(pages * WheelDelta), "wheel"),
      _ when string.Equals(direction, "down", StringComparison.OrdinalIgnoreCase) =>
        new TaggedMouseRow(0, MouseWheel, 0, 0, unchecked((uint)(-pages * WheelDelta)), "wheel"),
      _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "only up/down reach the wheel builder"),
    };
  }

  /// <summary>The wheel rows for one scroll: one row per page (pages >= 1), each a
  ///     single notch multiple.</summary>
  internal static TaggedMouseRow[] BuildWheelRows(string direction, int pages)
  {
    int count = Math.Max(1, pages);
    TaggedMouseRow row = BuildWheelRow(direction, 1);
    return [.. Enumerable.Repeat(row, count)];
  }

  /// <summary>The targeted click row sequence over the GIVEN extents (fake metrics -
  ///     the injected seam): a leading MOVE|ABSOLUTE|VIRTUALDESK row to the point, then
  ///     button down, then button up. Zero or negative extents yield NO rows - the
  ///     honest refusal - never a degenerate event.</summary>
  internal static TaggedMouseRow[] BuildClickRows(string button, int x, int y, int cx, int cy) =>
    BuildClickRows(button, x, y, new DesktopMetrics(cx, cy));

  /// <summary>The click row sequence over an origin-aware virtual desktop (see
  ///     <see cref="BuildMoveParts(int, int, DesktopMetrics)"/>).</summary>
  internal static TaggedMouseRow[] BuildClickRows(string button, int x, int y, DesktopMetrics desktop)
  {
    string normalized = button.ToUpperInvariant();
    (uint down, uint up) = normalized switch
    {
      "RIGHT" => (0x0008u, 0x0010u),
      "MIDDLE" => (0x0020u, 0x0040u),
      _ => (0x0002u, 0x0004u),
    };

    if (!desktop.IsUsable)
    {
      return [];
    }

    (uint moveFlags, int dx, int dy) = BuildMoveParts(x, y, desktop);
    return
    [
      Tag(moveFlags, dx, dy, "move"),
      new(0, down, 0, 0, 0, "down"),
      new(0, up, 0, 0, 0, "up"),
    ];
  }

  private static TaggedMouseRow Tag((uint flags, int dx, int dy) parts) => new(0, parts.flags, parts.dx, parts.dy, 0, "move");

  private static TaggedMouseRow Tag(uint flags, int dx, int dy, string tag) => new(0, flags, dx, dy, 0, tag);

  private static Input[] FromTaggedRows(TaggedMouseRow[] rows)
  {
    List<Input> inputs = [];
    foreach (TaggedMouseRow row in rows)
    {
      inputs.Add(new Input { _type = row.Type, _mouse = new Input.MouseRow { _dwFlags = row.Flags, _dx = row.Dx, _dy = row.Dy, _mouseData = row.MouseData } });
    }

    return [.. inputs];
  }

  private static Input[] FromDragRows(DragRow[] rows)
  {
    List<Input> inputs = [];
    foreach (DragRow row in rows)
    {
      inputs.Add(new Input { _type = row.Type, _mouse = new Input.MouseRow { _dwFlags = row.Flags, _dx = row.Dx, _dy = row.Dy } });
    }

    return [.. inputs];
  }
  /// <summary>Virtual-desktop extents (physical pixels); false when either is not positive
  ///     - the honest refusal for a degenerate desktop, never a divide-by-zero.</summary>
  /// <summary>The real virtual-desktop metrics; false when either extent is not
  ///     positive - the honest refusal for a degenerate desktop, never a
  ///     divide-by-zero.</summary>
  private static bool TryExtents(out DesktopMetrics desktop)
  {
    desktop = ReadDesktopMetrics();
    return desktop.IsUsable;
  }

  /// <summary>Reads the virtual-desktop extent and origin in physical pixels. The origin
  ///     is read alongside the extent because an absolute injection is normalized
  ///     against the virtual screen, not against the primary monitor.</summary>
  internal static DesktopMetrics ReadDesktopMetrics() => new(
    GetSystemMetrics(ExtentWidthMetric),
    GetSystemMetrics(ExtentHeightMetric),
    GetSystemMetrics(OriginXMetric),
    GetSystemMetrics(OriginYMetric));

  [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  [LibraryImport("user32.dll")]
  private static partial int GetSystemMetrics(int index);

  /// <summary>How many real SendInput batches this process has injected. Every real send
  ///     funnels through Inject, so this counter is the whole live-fire surface: a test
  ///     assembly snapshots it at start and fails if it grew.</summary>
  internal static int RealInjections => Volatile.Read(ref _realInjections);

  private static int _realInjections;

  private static bool Inject(Input[] rows)
  {
    if (rows.Length > 0)
    {
      _ = Interlocked.Increment(ref _realInjections);
    }

    return SendInput((uint)rows.Length, rows, Marshal.SizeOf<Input>()) == rows.Length;
  }

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
  internal static partial uint SendInput(uint nInputs, [In] Input[] pInputs, int cbSize);
}
