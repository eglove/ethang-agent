using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;

namespace eThangAgent.Roslyn.ACL.Tests;

/// <summary>Shell() starts a command the way the Windows loader would: a bare name is resolved
///     through PATH and PATHEXT first, so a command published as a .cmd shim runs, and a name it
///     cannot resolve is passed through untouched so the failure and its message are unchanged.
///     <para>Every test below builds its own shim and puts its directory at the front of PATH, so
///     what is asserted is the rule rather than this machine's installed tools.</para></summary>
[Trait("Category", "Process")]
[Collection("ProcessGlobalState")]
public class ShellExecutableResolutionTests
{
  [Fact]
  public void BareCmdShimOnPath_IsStartedAndReturnsItsOutput()
  {
    DirectoryInfo tmp = TestTempRoot.CreateDirectory("shell-pathext-cmd");
    _ = WriteShim(tmp.FullName, "probe-shim", "@echo off", "echo shim-ok");

    using (PrependPath(tmp.FullName))
    {
      ShellResult result = Globals().Shell("probe-shim");

      Assert.True(result.ExitCode == 0,
          $"expected exit 0 from a .cmd shim started by bare name; got {result.ExitCode}: {result.Stderr}");
      Assert.Equal("shim-ok", result.Stdout.Trim());
    }
  }

  [Fact]
  public void BareCmdShim_ReceivesEachArgumentAsOneNativeToken()
  {
    DirectoryInfo tmp = TestTempRoot.CreateDirectory("shell-pathext-args");
    _ = WriteShim(tmp.FullName, "probe-args", "@echo off", "echo [%~1]", "echo [%~2]");

    using (PrependPath(tmp.FullName))
    {
      ShellResult result = Globals().Shell("probe-args", "--version", "two words");

      Assert.True(result.ExitCode == 0, $"expected exit 0; got {result.ExitCode}: {result.Stderr}");
      Assert.Equal(["[--version]", "[two words]"], result.Stdout.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
    }
  }

  [Fact]
  public void BareCmdShim_PropagatesItsExitCode()
  {
    DirectoryInfo tmp = TestTempRoot.CreateDirectory("shell-pathext-exit");
    _ = WriteShim(tmp.FullName, "probe-exit", "@echo off", "exit /b 7");

    using (PrependPath(tmp.FullName))
    {
      Assert.Equal(7, Globals().Shell("probe-exit").ExitCode);
    }
  }

  [Fact]
  public void BareNameThatDoesNotResolve_ReportsTheOriginalName()
  {
    DirectoryInfo tmp = TestTempRoot.CreateDirectory("shell-pathext-missing");

    using (PrependPath(tmp.FullName))
    {
      ShellResult result = Globals().Shell("ethang-no-such-tool");

      Assert.NotEqual(0, result.ExitCode);
      Assert.Contains("'ethang-no-such-tool'", result.Stderr, StringComparison.Ordinal);
      Assert.Empty(result.Stdout);
    }
  }

  [Fact]
  public void BareExeOnPath_KeepsTheBehaviourItHad()
  {
    DirectoryInfo tmp = TestTempRoot.CreateDirectory("shell-pathext-exe");
    File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Path.Combine(tmp.FullName, "probe-exe.exe"));

    using (PrependPath(tmp.FullName))
    {
      ShellResult result = Globals().Shell("probe-exe", "/c", "echo exe-ok");

      Assert.True(result.ExitCode == 0, $"expected exit 0 from a bare .exe; got {result.ExitCode}: {result.Stderr}");
      Assert.Equal("exe-ok", result.Stdout.Trim());
    }
  }

  [Fact]
  public void QualifiedCmdPath_IsStartedWithoutADirectorySearch()
  {
    DirectoryInfo tmp = TestTempRoot.CreateDirectory("shell-pathext-qualified");
    string shim = WriteShim(tmp.FullName, "probe-direct", "@echo off", "echo direct-ok");

    ShellResult result = Globals().Shell(shim);

    Assert.True(result.ExitCode == 0, $"expected exit 0 from an absolute .cmd path; got {result.ExitCode}: {result.Stderr}");
    Assert.Equal("direct-ok", result.Stdout.Trim());
  }

  [Fact]
  public void QualifiedPathThatDoesNotExist_ReportsTheOriginalPath()
  {
    DirectoryInfo tmp = TestTempRoot.CreateDirectory("shell-pathext-absent");
    string absent = Path.Combine(tmp.FullName, "absent.cmd");

    ShellResult result = Globals().Shell(absent);

    Assert.NotEqual(0, result.ExitCode);
    Assert.Contains(absent, result.Stderr, StringComparison.Ordinal);
  }

  // ---- Fixtures ----

  private static ScriptGlobals Globals() => new(NoopRegistry(), TestTempRoot.Root, TestTempRoot.Root);

  /// <summary>Writes a batch file and returns its absolute path.</summary>
  private static string WriteShim(string directory, string name, params string[] lines)
  {
    string path = Path.Combine(directory, name + ".cmd");
    File.WriteAllLines(path, lines);
    return path;
  }

  /// <summary>Puts <paramref name="directory"/> at the front of PATH for the life of the
  ///     returned scope, restoring the previous value on dispose. Prepending rather than
  ///     replacing keeps every other command a concurrent test spawns resolvable.</summary>
  private static PathScope PrependPath(string directory)
  {
    string? previous = Environment.GetEnvironmentVariable("PATH");
    Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + previous);
    return new PathScope(previous);
  }

  private sealed class PathScope(string? previous) : IDisposable
  {
    public void Dispose() => Environment.SetEnvironmentVariable("PATH", previous);
  }

  // Shell never resolves tools or invokes capabilities; an empty registry stub
  // satisfies ScriptTools' non-null contract.
  private static EmptyRegistry NoopRegistry() => new();

  private sealed class EmptyRegistry : ICapabilityRegistry
  {
    public Result<ResolvedCapability> Resolve(string nameOrRef) =>
        Result.Failure<ResolvedCapability>(new DomainError("NotFound", "unused"));

    public IReadOnlyList<ProviderCapabilities> Providers => [];

    public Task<CapabilityInvocationResult> InvokeAsync(
        ResolvedCapability capability, string jsonArguments, CancellationToken ct = default) =>
        throw new NotSupportedException("unused");
  }
}
