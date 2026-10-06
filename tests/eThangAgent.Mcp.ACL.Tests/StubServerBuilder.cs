using System.Diagnostics;

namespace eThangAgent.Mcp.ACL.Tests;

/// <summary>Builds the in-repo stub MCP server (tests/StubServer) once per test
///     process and returns its exe path. The stub is a REAL MCP stdio server with
///     echo / dump_env / chatty / crash tools (see StubServer/Program.cs).</summary>
internal static class StubServerBuilder
{
  private static readonly Lock Gate = new();
  private static string? _serverExe;

  public static string Build()
  {
    lock (Gate)
    {
      if (_serverExe is not null && File.Exists(_serverExe))
      {
        return _serverExe;
      }

      string project = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "StubServer", "stub.csproj");
      string resolved = Path.GetFullPath(project);
      ProcessStartInfo psi = new("dotnet", "build " + resolved + " -v q --nologo")
      {
        RedirectStandardOutput = true,
        UseShellExecute = false,
      };
      using Process build = Process.Start(psi)!;
      _ = build.StandardOutput.ReadToEndAsync();
      build.WaitForExit();
      Assert.True(build.ExitCode == 0, "stub MCP server build failed");
      _serverExe = Path.Combine(Path.GetDirectoryName(resolved)!, "bin", "Debug", "net10.0", "ethang-mcp-stubserver.exe");
      return _serverExe;
    }
  }
}
