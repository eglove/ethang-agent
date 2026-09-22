using eThangAgent.ToolDomain;

using System.Runtime.InteropServices;

namespace eThangAgent.ComputerUse.ACL.Tests;

/// <summary>One owned Win32 test window for the whole integration collection: created
///     once per test class run, driving assertions from the window's own state.</summary>
public sealed partial class IntegrationWindowFixture : IDisposable
{
  public Win32TestWindow Window { get; } = new();

  public nint WindowHandleId => Window.Handle;

  /// <summary>Runs one observe against the window through the given access and returns
  ///     the observation, registering the state in the access's ledger.</summary>
  public async Task<ComputerOutcome.Observation> ObserveAsync(BrokerComputerAccess access, CancellationToken ct)
  {
    ArgumentNullException.ThrowIfNull(access);
    ComputerOutcome outcome = await access.ExecuteAsync(
        new ComputerCommand.Observe(ComputerAppRef.ByPid(Window.Pid), IncludeScreenshot: false, DisableDiffing: false),
        ct).ConfigureAwait(true);
    return Assert.IsType<ComputerOutcome.Observation>(outcome);
  }

  /// <summary>The first element index matching the predicate, or -1.</summary>
  public static int IndexOf(ComputerOutcome.Observation observation, Func<ComputerElement, bool> predicate)
  {
    ArgumentNullException.ThrowIfNull(observation);
    ArgumentNullException.ThrowIfNull(predicate);
    return
      observation.Elements.Where(predicate).Select(e => (int?)e.Index).FirstOrDefault() ?? -1;
  }

  /// <summary>The button's center in DELIVERED-RASTER pixel coordinates, given the
  ///     observation's frame reference and the window's screen bounds.</summary>
  public (int X, int Y) ButtonCenterIn(ComputerFrameRef frame)
  {
    ArgumentNullException.ThrowIfNull(frame);
    NativeRect window = ScreenRectOf(Window.Handle);
    NativeRect button = ScreenRectOf(Window.ButtonHandle);
    double fx = (((button._left + button._right) / 2.0) - window._left) / window.Width;
    double fy = (((button._top + button._bottom) / 2.0) - window._top) / window.Height;
    return ((int)(fx * frame.Width), (int)(fy * frame.Height));
  }

  [LibraryImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static partial bool SetForegroundWindow(nint hwnd);

  [LibraryImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  private static partial nint GetForegroundWindow();

  /// <summary>Controller ruling (coordinate-click safety): takes the foreground and verifies it.
  ///     True only when GetForegroundWindow() == our window.</summary>
  public bool TryTakeForeground()
  {
    _ = SetForegroundWindow(Window.Handle);
    Thread.Sleep(50);
    return GetForegroundWindow() == Window.Handle;
  }

  public void Dispose() => Window.Dispose();

  [StructLayout(LayoutKind.Sequential)]
  private struct NativeRect
  {
    public int _left;
    public int _top;
    public int _right;
    public int _bottom;
    public readonly int Width => _right - _left;
    public readonly int Height => _bottom - _top;
  }

  [LibraryImport("user32.dll", EntryPoint = "GetWindowRect"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static partial bool GetScreenRect(nint hwnd, out NativeRect rect);

  private static NativeRect ScreenRectOf(nint hwnd)
  {
    bool ok = GetScreenRect(hwnd, out NativeRect rect);
    return ok ? rect : throw new InvalidOperationException("GetWindowRect failed for " + hwnd);
  }
}
