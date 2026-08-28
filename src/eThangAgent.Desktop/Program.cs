using System.Runtime.Versioning;
using Avalonia;

// Named decision: the app is Windows-only by design (AGENTS.md) — path handling,
// process execution, and the DPAPI key protector all assume it. Declaring that at
// the assembly level lets the platform analyzers verify callers instead of
// flagging every Windows-specific call site.
[assembly: SupportedOSPlatform("windows")]

namespace eThangAgent.Desktop;

internal static class Program
{
  [STAThread]
  public static void Main(string[] args) => BuildAvaloniaApp()
      .StartWithClassicDesktopLifetime(args);

  public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
      .UsePlatformDetect()
      .WithInterFont()
      .LogToTrace();
}
