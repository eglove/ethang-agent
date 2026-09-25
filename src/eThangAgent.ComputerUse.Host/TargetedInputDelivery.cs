using System.Runtime.InteropServices;

namespace eThangAgent.ComputerUse.Host;

/// <summary>Targeted input delivery (opt-in, `--input-targeted`): the send hooks deliver
///     input as WINDOW MESSAGES to the verified target's focused window — never as
///     global SendInput. Every gate still runs on the real foreground read (strategy=event,
///     the paste gate), so a delivery is always addressed to the window the controller
///     verified as foreground: chords and text arrive at the focus window (falling back
///     to the foreground window itself), and a positioned click resolves the child window
///     at the resolved point and delivers the button pair to it. Drag and wheel are
///     unsupported and answer false — the dispatcher's honest send-failure. This mode
///     exists so the integration suite drives the REAL broker end to end (pipe, gates,
///     UIA, element ops) while no injected input can ever leave the target window, on
///     any monitor.</summary>
internal static partial class TargetedInputDelivery
{
  public static bool SendChord(KeyChord chord)
  {
    nint target = FocusTarget();
    if (target == nint.Zero)
    {
      return false;
    }

    // Printable no-modifier chords travel as WM_CHAR (the message a focused edit or
    // window proc consumes); everything else degrades to the key-down/key-up pair.
    if (chord.Modifiers == KeyModifiers.None && CharFor(chord.KeyCode) is { } ch)
    {
      return PostMessageW(target, WmChar, ch, nint.Zero);
    }

    _ = PostMessageW(target, WmKeyDown, chord.KeyCode, nint.Zero);
    return PostMessageW(target, WmKeyUp, chord.KeyCode, nint.Zero);
  }

  public static bool SendText(string text)
  {
    nint target = FocusTarget();
    return target != nint.Zero && text.All(ch => PostMessageW(target, WmChar, ch, nint.Zero));
  }

  public static bool SendMouseButtonAt(string button, int x, int y)
  {
    nint foreground = GetForegroundWindow();
    if (foreground == nint.Zero)
    {
      return false;
    }

    // The resolved global (physical-pixel) point lands in the foreground window's
    // client space; the child window at that point (the test fixture's button) is the
    // real recipient, exactly as the hit-test of a physical click would resolve it.
    Point clientPoint = new(x, y);
    _ = ScreenToClient(foreground, ref clientPoint);
    nint target = RealChildWindowFromPoint(foreground, clientPoint);
    if (target == nint.Zero)
    {
      return false;
    }

    Point targetPoint = new(x, y);
    _ = ScreenToClient(target, ref targetPoint);
    nint lParam = MakeLParam(targetPoint._x, targetPoint._y);
    (uint down, uint up, nint wParam) = button switch
    {
      "right" => (WmRButtonDown, WmRButtonUp, MkRButton),
      "middle" => (WmMButtonDown, WmMButtonUp, MkMButton),
      _ => (WmLButtonDown, WmLButtonUp, MkLButton),
    };
    return PostMessageW(target, down, wParam, lParam)
        && PostMessageW(target, up, 0, lParam);
  }

  public static bool SendDrag(string button, int fx, int fy, int tx, int ty)
  {
    _ = (button, fx, fy, tx, ty);
    return false; // honest unsupported: a drag cannot be delivered as targeted messages
  }

  public static bool SendWheelAt(string direction, int pages, int x, int y)
  {
    _ = (direction, pages, x, y);
    return false; // honest unsupported: the wheel is a global input, never targeted
  }

  /// <summary>The focused window of the foreground window's thread, falling back to the
  ///     foreground window itself (a window with no child focus receives its own keys).</summary>
  private static nint FocusTarget()
  {
    nint foreground = GetForegroundWindow();
    if (foreground == nint.Zero)
    {
      return nint.Zero;
    }

    GuiThreadInfo info = new() { _cbSize = (uint)Marshal.SizeOf<GuiThreadInfo>() };
    uint thread = GetWindowThreadProcessId(foreground, out _);
    return GetGUIThreadInfo(thread, ref info) && info._hwndFocus != nint.Zero
        ? info._hwndFocus
        : foreground;
  }

  private static char? CharFor(ushort keyCode) => keyCode switch
  {
    // An unshifted letter key produces its lowercase character (the VK code carries
    // the uppercase form).
    >= 'A' and <= 'Z' => char.ToLowerInvariant((char)keyCode),
    >= '0' and <= '9' => (char)keyCode,
    0x20 => ' ',
    0x0D => '\r',
    0x09 => '\t',
    0x08 => '\b',
    _ => null,
  };

  private static nint MakeLParam(int low, int high) => (low & 0xFFFF) | ((high & 0xFFFF) << 16);

  [StructLayout(LayoutKind.Sequential)]
  private struct Point(int x, int y)
  {
    public int _x = x;
    public int _y = y;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct GuiThreadInfo
  {
    public uint _cbSize;
    public uint _flags;
    public nint _hwndActive;
    public nint _hwndFocus;
    public nint _hwndCapture;
    public nint _hwndMenuOwner;
    public nint _hwndMoveSize;
    public nint _hwndCaret;
    public Rect _rcCaret;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct Rect(int left, int top, int right, int bottom)
  {
    public int _left = left;
    public int _top = top;
    public int _right = right;
    public int _bottom = bottom;
  }

  private const uint WmChar = 0x0102;
  private const uint WmKeyDown = 0x0100;
  private const uint WmKeyUp = 0x0101;
  private const uint WmLButtonDown = 0x0201;
  private const uint WmLButtonUp = 0x0202;
  private const uint WmRButtonDown = 0x0204;
  private const uint WmRButtonUp = 0x0205;
  private const uint WmMButtonDown = 0x0207;
  private const uint WmMButtonUp = 0x0208;
  private const nint MkLButton = 0x0001;
  private const nint MkRButton = 0x0002;
  private const nint MkMButton = 0x0010;

  [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  [LibraryImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static partial bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

  // LibraryImport rejects the marshaling shapes of the remaining imports (nested
  // structs, ref/out params), so they ride classic DllImport — the same pattern
  // RealBrokerObserver uses.
  // Named decision (SYSLIB1054/CA1838 precedent, T12-13): LibraryImport cannot marshal
  // these shapes (nested private structs, ref params); classic DllImport with
  // blittable structs marshals identically here.
#pragma warning disable SYSLIB1054, CA1838
  [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  private static extern nint GetForegroundWindow();

  [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  private static extern uint GetWindowThreadProcessId(nint hWnd, out int lpdwProcessId);

  [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool ScreenToClient(nint hwnd, ref Point point);

  [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  private static extern nint RealChildWindowFromPoint(nint hwndParent, Point point);

  [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetGUIThreadInfo(uint idThread, ref GuiThreadInfo info);
#pragma warning restore SYSLIB1054, CA1838
}
