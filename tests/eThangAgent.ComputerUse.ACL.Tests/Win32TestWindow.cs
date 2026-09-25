using System.Runtime.InteropServices;

namespace eThangAgent.ComputerUse.ACL.Tests;

/// <summary>The test process's OWN Win32 window (the only surface the integration
///     suite touches): RegisterClassEx + CreateWindowEx with one child BUTTON and one
///     child EDIT. The WndProc records every command that arrives, so element clicks,
///     coordinate clicks, and key sends are asserted from the window's own state -
///     never from a broker receipt alone. No unsafe code.</summary>
public sealed partial class Win32TestWindow : IDisposable
{
  public static readonly string ClassName = "EthangCuTestWindow-" + Guid.NewGuid().ToString("N")[..8];
  public const string WindowTitle = "eThang Computer Use Integration Test Window";
  public const string ButtonCaption = "Count Me";
  public const string InitialEditText = "initial";

  public nint Handle { get; private set; }

  public nint ButtonHandle { get; private set; }

  public nint EditHandle { get; private set; }

  public int Pid { get; } = Environment.ProcessId;

  // WndProc-recorded facts the assertions read.
  private int _clickCount;
  private readonly List<string> _chars = [];

  public int ClickCount => Interlocked.CompareExchange(ref _clickCount, 0, 0);

  public string LastEditText
  {
    get
    {
      nint len = SendMessage(EditHandle, WmGetTextLength, 0, 0);
      char[] buffer = new char[len + 1];
      _ = SendMessage(EditHandle, WmGetText, len + 1, buffer);
      return new string(buffer, 0, (int)len);
    }
  }

  public string RecordedChars
  {
    get
    {
      lock (_chars)
      {
        return string.Concat(_chars);
      }
    }
  }


  /// <summary>Diagnostic: timestamp of the last message the loop dispatched (updated every
  ///     iteration) — proves the pump thread is alive.</summary>
  public long LastPumpTicks
  {
    get
    {
      lock (_pumpGate)
      {
        return LastPumpTicksValue;
      }
    }
  }

  private readonly Lock _pumpGate = new();
  private long LastPumpTicksValue { get; set; }

  /// <summary>The pump thread's fault, if any (creation-phase faults rethrow;
  /// loop-phase faults used to die silently with the thread - they are stored here).</summary>
  private Exception? PumpFault { get; set; }

  /// <summary>Human-readable pump health for assertion messages and diagnostics.</summary>
  internal string PumpDiagnostics => PumpFault is null ? "no fault" : "pump fault: " + PumpFault;


  /// <summary>Diagnostics for the controller ruling: creation-time foreground snapshot.</summary>
  public nint CreationForeground { get; private set; }

  public int CreationForegroundPid { get; private set; }

  public string CreationForegroundProcess { get; private set; } = string.Empty;

  public uint CreatorThreadId { get; private set; }

  public bool CreationTimeIsWindow { get; private set; }

  public Win32TestWindow()
  {
    using ManualResetEventSlim created = new(false);
    Exception? fault = null;
    Thread thread = new(() =>
    {
      try
      {
        RunMessageLoop(created);
      }
#pragma warning disable CA1031 // The worker boundary must surface any fault to the constructing thread.
      catch (Exception ex)
      {
        fault = ex;
      }
#pragma warning restore CA1031
      finally
      {
        created.Set();
      }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.IsBackground = true;
    thread.Start();
    if (!created.Wait(TimeSpan.FromSeconds(10)))
    {
      throw new TimeoutException("test window was not created in time.");
    }

    if (fault is not null)
    {
      throw new InvalidOperationException("test window creation failed.", fault);
    }
  }

  private void RunMessageLoop(ManualResetEventSlim created)
  {
    _wndProcHolder = WndProcRouter;
    NativeClassEx wc = new()
    {
      _cbSize = (uint)Marshal.SizeOf<NativeClassEx>(),
      _lpfnWndProc = _wndProcHolder,
      _hInstance = GetModuleHandle(null),
      _lpszClassName = ClassName,
      _hbrBackground = 5, // COLOR_WINDOW
    };
    ushort atom = RegisterClassEx(ref wc);
    if (atom == 0 && Marshal.GetLastWin32Error() != 1410)
    {
      // 1410 ERROR_CLASS_ALREADY_EXISTS: the per-process class from an earlier instance is fine.
      throw new InvalidOperationException("RegisterClassEx failed: " + Marshal.GetLastWin32Error());
    }

    Handle = CreateWindowEx(ExToolWindow, ClassName, WindowTitle, WsPopup | WsVisible,
      60, 60, 520, 400, 0, 0, wc._hInstance, 0);
    if (Handle == 0)
    {
      throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
    }
    WindowsByHandle[Handle] = this;

    ButtonHandle = CreateWindowEx(0, "Button", ButtonCaption, WsChild | WsVisible,
      20, 20, 140, 34, Handle, 0, wc._hInstance, 0);
    EditHandle = CreateWindowEx(0, "Edit", InitialEditText, WsChild | WsVisible | WsBorder | EsAutoHScroll,
      20, 70, 440, 28, Handle, 0, wc._hInstance, 0);
    _ = ShowWindow(Handle, SwShow);
    _ = UpdateWindow(Handle);
    _ = SetWindowText(Handle, WindowTitle);
    CreatorThreadId = GetCurrentThreadId();
    CreationTimeIsWindow = IsWindowAlive(Handle);
    CreationForeground = GetForegroundWindow();
    CreationForegroundPid = PidOfWindow(CreationForeground);
    CreationForegroundProcess = ProcessNameOfPid(CreationForegroundPid);
    created.Set();

    while (!_stopLoop)
    {
      try
      {
        // Peek-based pump: GetMessage+DispatchMessage without a quit dependency, so an external
        // WM_DESTROY of ONE window cannot end the thread and destroy every other window on it.
        lock (_pumpGate)
        {
          LastPumpTicksValue = DateTime.UtcNow.Ticks;
        }
        lock (_pumpGate)
        {
          LastPumpTicksValue = DateTime.UtcNow.Ticks;
        }
        _ = Interlocked.Increment(ref _aliveChecks);
        if (!IsWindowAlive(Handle))
        {
          // The live desktop (or another agent session's automation) closed our window: recreate
          // it in place so the fixture keeps serving observations.
          RecreateWindow();
        }

        bool any = PeekMessage(out NativeMsg msg, 0, 0, 0, PmRemove);
        if (any)
        {
          _ = TranslateMessage(msg);
          _ = DispatchMessage(msg);
        }
        else
        {
          Thread.Sleep(10);
        }
      }
#pragma warning disable CA1031 // Named decision: the STA loop boundary must never crash the process; the fault is stored for diagnostics.
      catch (Exception ex)
      {
        // The STA thread must never crash the process, but its death destroys this
        // window; store the fault so tests can name the cause instead of guessing.
        PumpFault = ex;
        _stopLoop = true;
      }
#pragma warning restore CA1031
    }
  }

  private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
  {
    if (msg == WmKill)
    {
      _stopLoop = true;
      return 0;
    }

    if (msg == WmClose)
    {
      // The integration window must survive stray close requests from the environment:
      // swallow WM_CLOSE here. Dispose sends the private WmKill instead.
      return 0;
    }

    // WM_COMMAND: HIWORD(wParam) is the notification code (BN_CLICKED = 0); lParam is
    // the control's HWND. The old check read HIWORD(lParam) — the HWND's high bits —
    // which accidentally equals zero only while HWND values fit in 16 bits; modern
    // handle values exceed 0xFFFF and silenced every click record.
    if (msg == WmCommand && lParam != 0 && HiWord((uint)wParam) == BnClicked)
    {
      _ = Interlocked.Increment(ref _clickCount);
    }
    else if (msg == WmChar)
    {
      lock (_chars)
      {
        _chars.Add(((char)wParam).ToString());
      }
    }
    else if (msg == WmDestroy)
    {
      // Nothing: the loop stops only via WmKill, surviving stray destroys of OTHER windows.
    }

    return DefWindowProc(hwnd, msg, wParam, lParam);
  }


  public static void Pump(int milliseconds = 150) =>
    // The window's message loop lives on its own thread; a client-side pump only
    // yields. Sleep lets the loop drain queued input events before an assertion.
    Thread.Sleep(milliseconds);

  private static int HiWord(uint value) => (int)(value >> 16) & 0xffff;


  /// <summary>Diagnostic: the window title as read by SendMessage(WM_GETTEXTTEXT) on the
  ///     window's own thread — bypasses cross-thread GetWindowText internals.</summary>
  public string TitleViaSelf
  {
    get
    {
      nint len = SendMessage(Handle, WmGetTextLength, 0, 0);
      char[] buffer = new char[len + 1];
      _ = SendMessage(Handle, WmGetText, len + 1, buffer);
      return new string(buffer, 0, (int)len);
    }
  }
  /// <summary>Recreates the destroyed window (and children) on the pump thread so the
  ///     fixture keeps serving despite external closes.</summary>
  private void RecreateWindow()
  {
    _ = Interlocked.Increment(ref _recreateCount);
    nint fresh = CreateWindowEx(0, ClassName, WindowTitle, WsOverlappedWindow | WsVisible,
      60, 60, 520, 400, 0, 0, GetModuleHandle(null), 0);
    if (fresh == 0)
    {
      LastRecreateError = Marshal.GetLastWin32Error();
      return;
    }

    if (Handle != 0)
    {
      _ = WindowsByHandle.TryRemove(Handle, out _);
    }

    Handle = fresh;
    WindowsByHandle[Handle] = this;
    ButtonHandle = CreateWindowEx(0, "Button", ButtonCaption, WsChild | WsVisible,
      20, 20, 140, 34, Handle, 0, GetModuleHandle(null), 0);
    EditHandle = CreateWindowEx(0, "Edit", InitialEditText, WsChild | WsVisible | WsBorder | EsAutoHScroll,
      20, 70, 440, 28, Handle, 0, GetModuleHandle(null), 0);
    _ = ShowWindow(Handle, SwShow);
    _ = UpdateWindow(Handle);
  }

  [LibraryImport("user32.dll", EntryPoint = "DestroyWindow"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static partial bool DestroyWindow(nint hwnd);

  [LibraryImport("user32.dll", EntryPoint = "IsWindow"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static partial bool IsWindowAlive(nint hwnd);


  /// <summary>Diagnostic: IsWindow result read on the pump thread right after creation.</summary>



  /// <summary>Diagnostic: the last RecreateWindow failure code (0 = none/never ran).</summary>
  public int LastRecreateError { get; private set; }

  /// <summary>Diagnostic: how many recreations ran.</summary>
  public int RecreateCount => Interlocked.CompareExchange(ref _recreateCount, 0, 0);

  private int _recreateCount;
  private int _aliveChecks;

  /// <summary>Diagnostic: how many pump iterations checked window liveness.</summary>
  public int AliveChecks => Interlocked.CompareExchange(ref _aliveChecks, 0, 0);

  public bool StopLoopRequested => _stopLoop;


  /// <summary>Diagnostic/test: destroys the current window so the pump recreates it.</summary>
  public void SimulateDestruction() => DestroyWindow(Handle);

  /// <summary>Diagnostic/test: posts WM_NULL so the pump must retrieve and dispatch a message.</summary>
  internal void PostProbeMessage() => _ = PostMessage(Handle, 0x0000, 0, 0);

  /// <summary>Diagnostic/test: IsWindow from the test thread.</summary>

  /// <summary>Diagnostic: FindWindowW by class name.</summary>
  public static nint FindByClassNow()
  {
    nint found = FindWindowByClassName(ClassName);
    return found;
  }

  [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  private static extern nint FindWindowByClassName(string className, nint windowName = default);

  public bool IsWindowAliveNow() => IsWindowAlive(Handle);

  public void Dispose()
  {
    if (Handle != 0)
    {
      _ = PostMessage(Handle, WmKill, 0, 0);
      _ = WindowsByHandle.TryRemove(Handle, out _);
    }
  }

  private static WndProcDelegate? _wndProcHolder;

  private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

  // The window class registers ONE wndproc per process, so messages of EVERY
  // instance arrive through the first-registered delegate. Route by handle so a
  // second instance (a regression probe) can never stop another instance's pump.
  private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, Win32TestWindow> WindowsByHandle = new();

  private static readonly WndProcDelegate WndProcRouter = RouteMessage;

  private static nint RouteMessage(nint hwnd, uint msg, nint wParam, nint lParam) =>
    WindowsByHandle.TryGetValue(hwnd, out Win32TestWindow? owner)
      ? owner.WndProc(hwnd, msg, wParam, lParam)
      : DefWindowProc(hwnd, msg, wParam, lParam);

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  private struct NativeClassEx
  {
    public uint _cbSize;
    public uint _style;
    public WndProcDelegate? _lpfnWndProc;
    public int _cbClsExtra;
    public int _cbWndExtra;
    public nint _hInstance;
    public nint _hIcon;
    public nint _hCursor;
    public nint _hbrBackground;
    public string? _lpszMenuName;
    public string _lpszClassName;
    public nint _hIconSm;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct NativeMsg
  {
    public nint _hwnd;
    public uint _message;
    public nint _wParam;
    public nint _lParam;
    public uint _time;
    public int _ptX;
    public int _ptY;
  }

  private const uint WsOverlappedWindow = 0x00CF0000;
  private const uint WsPopup = 0x80000000;
  private const uint ExToolWindow = 0x00000080;
  private const uint WsVisible = 0x10000000;
  private const uint WsChild = 0x40000000;
  private const uint WsBorder = 0x00800000;
  private const uint EsAutoHScroll = 0x0080;
  private const uint WmDestroy = 0x0002;
  private const uint WmClose = 0x0010;
  private const uint WmKill = 0x8000; // WM_APP range sentinel
  private const uint WmCommand = 0x0111;
  private const uint WmChar = 0x0102;
  private const uint WmGetText = 0x000D;
  private const uint WmGetTextLength = 0x000E;
  private const int BnClicked = 0;
  private const int SwShow = 5;
  private const uint PmRemove = 1;
  private volatile bool _stopLoop;

  [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  private static partial nint GetModuleHandle(string? name);
#pragma warning disable SYSLIB1054 // Named decision (T12-13 precedent): LibraryImport cannot marshal the WNDCLASSEX layout (function pointer member); DllImport with the blittable layout marshals identically here.
  [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  private static extern ushort RegisterClassEx(ref NativeClassEx wc);
#pragma warning restore SYSLIB1054

  [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  private static partial nint CreateWindowEx(uint exStyle, string className, string windowName, uint style,
    int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
  [LibraryImport("user32.dll", EntryPoint = "GetForegroundWindow"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  private static partial nint GetForegroundWindow();


  private static int PidOfWindow(nint hwnd)
  {
    _ = GetWindowThreadProcessId(hwnd, out int pid);
    return pid;
  }

  private static string ProcessNameOfPid(int pid)
  {
    try
    {
      using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(pid);
      return process.ProcessName;
    }
    catch (Exception ex) when (ex is ArgumentException or System.ComponentModel.Win32Exception)
    {
      return "<gone>";
    }
  }

  [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentThreadId"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  private static partial uint GetCurrentThreadId();

  [LibraryImport("user32.dll", EntryPoint = "PeekMessageW"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static partial bool PeekMessage(out NativeMsg msg, nint hwnd, uint filterMin, uint filterMax, uint remove);


  [LibraryImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static partial bool ShowWindow(nint hwnd, int cmd);

  [LibraryImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static partial bool UpdateWindow(nint hwnd);

  [LibraryImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static partial bool TranslateMessage(in NativeMsg msg);


  [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  private static partial nint DefWindowProc(nint hwnd, uint msg, nint wParam, nint lParam);

  [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  private static partial nint DispatchMessage(in NativeMsg msg);

  [LibraryImport("user32.dll", EntryPoint = "SendMessageW"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  private static partial nint SendMessage(nint hwnd, uint msg, nint wParam, nint lParam);
#pragma warning disable SYSLIB1054 // Named decision (T12-13 precedent): LibraryImport cannot marshal StringBuilder; DllImport with CharSet.Unicode marshals identically here.
  [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  private static extern nint SendMessage(nint hwnd, uint msg, nint wParam, [Out] char[] text);
#pragma warning restore SYSLIB1054

  [LibraryImport("user32.dll", EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static partial bool SetWindowText(nint hwnd, string text);

  [LibraryImport("user32.dll", EntryPoint = "PostMessageW"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  [return: MarshalAs(UnmanagedType.Bool)]
  internal static partial bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);

  [LibraryImport("user32.dll", EntryPoint = "GetWindowThreadProcessId"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  private static partial uint GetWindowThreadProcessId(nint hwnd, out int lpdwProcessId);


}
