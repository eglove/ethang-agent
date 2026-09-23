using System.Diagnostics;

namespace eThangAgent.ComputerUse.ACL.Tests;

/// <summary>Builds the in-repo stub broker host (tests/StubHost) once per test process and
///     returns its exe path. The stub serves authenticate + hello and echoes canned replies;
///     a request whose method is 'crash' makes it exit without replying.</summary>
internal static class StubHostBuilder
{
  private static readonly Lock Gate = new();
  private static string? _hostExe;

  public static string Build()
  {
    lock (Gate)
    {
      if (_hostExe is not null && File.Exists(_hostExe))
      {
        return _hostExe;
      }

      string project = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "StubHost", "stub.csproj");
      string resolved = Path.GetFullPath(project);
      ProcessStartInfo psi = new("dotnet", "build " + resolved + " -v q --nologo")
      {
        RedirectStandardOutput = true,
        UseShellExecute = false,
      };
      using Process build = Process.Start(psi)!;
      _ = build.StandardOutput.ReadToEndAsync();
      build.WaitForExit();
      Assert.True(build.ExitCode == 0, "stub host build failed");
      _hostExe = Path.Combine(Path.GetDirectoryName(resolved)!, "bin", "Debug", "net10.0", "ethang-cu-stubhost.exe");
      return _hostExe;
    }
  }
}
