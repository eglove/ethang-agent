using System.Diagnostics;
using eThangAgent.TestSupport;

namespace eThangAgent.FileSystem.ACL.Tests;

/// <summary>Builds the orphan-writer host once per test process and reports its executable.
///     <para>Its own type rather than a bare Process call, so the one build and the one cache
///     live in one place: two crash tests in this suite must not each pay for a compile, and the
///     executable path is derived from the project the same way every in-repo stub host does.
///     </para></summary>
internal static class OrphanHostBuilder
{
  private static readonly Lock Gate = new();
  private static readonly Dictionary<string, string> Built = [];

  /// <summary>Compiles <paramref name="projectPath"/> if this process has not already, and
  ///     returns the executable the build produced.</summary>
  public static string Build(string projectPath)
  {
    lock (Gate)
    {
      if (Built.TryGetValue(projectPath, out string? known))
      {
        return known;
      }

      ProcessStartInfo build = new("dotnet", ["build", projectPath, "-v", "q", "--nologo"])
      {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
      };
      using Process process = Process.Start(build)!;
      string output = process.StandardOutput.ReadToEnd();
      string error = process.StandardError.ReadToEnd();
      process.WaitForExit();
      Assert.True(process.ExitCode == 0, $"the orphan writer did not build: {output}{error}");
      string exe = OrphanCrashHost.Executable(projectPath);
      Assert.True(File.Exists(exe), $"the orphan writer built but its executable is not at '{exe}'");
      Built[projectPath] = exe;
      return exe;
    }
  }
}
