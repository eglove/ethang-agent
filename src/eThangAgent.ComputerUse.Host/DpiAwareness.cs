using System.Runtime.InteropServices;

namespace eThangAgent.ComputerUse.Host;

/// <summary>W-c: declares PerMonitorV2 DPI awareness so all coordinate math (bounds,
///     clicks, capture rasters) runs on physical pixels. Idempotent; a FALSE return means
///     the OS refused (already set to a different mode) - the process keeps running and the
///     note is visible in the broker log line written by Program.</summary>
public static partial class DpiAwareness
{
  [LibraryImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static partial bool SetProcessDpiAwarenessContext(int value);

  public static bool SetPerMonitorV2() => SetProcessDpiAwarenessContext(-4 /* DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 */);
}
