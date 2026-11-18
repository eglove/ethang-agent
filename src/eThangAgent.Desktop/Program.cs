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
  /// <summary>Runs the app: a launch that names a workspace another process already has open
  ///     signals that process and exits with <see cref="LaunchHandoff.HandedOffExitCode"/>,
  ///     and every other launch starts on the launcher.</summary>
  [STAThread]
  public static int Main(string[] args) => Launch(args);

  /// <summary>The launch decision, then the app. A handoff creates no window and returns the
  ///     handoff code; the message goes to stderr because the process that was raised owns the
  ///     console's foreground.</summary>
  /// <param name="args">The launch's arguments; the first is the workspace, when one is named.
  ///     </param>
  internal static int Launch(string[] args)
  {
    LaunchHandoffResult decision = LaunchHandoff.Resolve(args is [var first, ..] ? first : null);
    if (decision.Message is { } message)
    {
      Console.Error.WriteLine(message);
    }

    return decision.IsHandedOff
        ? decision.ExitCode
        : BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
  }

  public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
      .UsePlatformDetect()
      .WithInterFont()
      .LogToTrace();
}
