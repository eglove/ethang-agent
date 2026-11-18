using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;

namespace eThangAgent.Roslyn.ACL.Tests;

/// <summary>Every tool the harness can ANNOUNCE is one this exec surface can RUN (plan #209,
///     step 6).
///     <para>The announcement and the spawn are decided by two different pieces - a name is
///     announced because <see cref="PathToolDiscovery.Resolve"/> classified its file, and it is
///     started because <see cref="ExecutableResolution"/> found it through PATH and PATHEXT - so
///     nothing but a test that does BOTH can prove they agree. The failure this rules out is the
///     one that existed before the shim fix: a prompt naming npm while <c>Shell("npm")</c>
///     reported 'The system cannot find the file specified'.</para>
///     <para>The tools are built here rather than borrowed from this machine, so the claim holds
///     on a machine that has none of them installed and no test depends on what happens to be
///     published.</para></summary>
[Trait("Category", "Process")]
[Collection("ProcessGlobalState")]
public class AnnouncedToolRunnabilityTests
{
  [Fact]
  public async Task EveryAnnouncedTool_StartsThroughShellWithExitCodeZero()
  {
    // One directory holding one tool of each kind the discovery lists - a .cmd script and a
    // console .exe - so the rule is checked for both paths the announcement can take.
    DirectoryInfo toolDirectory = TestTempRoot.CreateDirectory("announced-runnable");
    string script = UniqueName();
    string program = UniqueName();
    await File.WriteAllLinesAsync(Path.Combine(toolDirectory.FullName, script + ".cmd"),
        ["@echo off", "exit /b 0"], TestContext.Current.CancellationToken);
    File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
        Path.Combine(toolDirectory.FullName, program + ".exe"));
    string? previousPath = Environment.GetEnvironmentVariable("PATH");
    Environment.SetEnvironmentVariable("PATH", toolDirectory.FullName + Path.PathSeparator + previousPath);
    try
    {
      Assert.Equal([script, program], Announced([script, program]));

      foreach (string name in (string[])[script, program])
      {
        ExecRunResult run = await Engine().ExecuteAsync(
            new ExecProgram($"return Shell(\"{name}\", \"/c\", \"exit 0\").ExitCode.ToString();"),
            TestContext.Current.CancellationToken);

        Assert.True(run.Output.Trim() == "0",
            $"announced '{name}' did not start through Shell: exit {run.Output.Trim()} " +
            string.Join(';', run.ErrorLines));
      }
    }
    finally
    {
      Environment.SetEnvironmentVariable("PATH", previousPath);
      DeleteDirectory(toolDirectory);
    }
  }

  [Fact]
  public async Task ACmdShimThatResolves_IsAnnouncedAndReturnsItsOutput()
  {
    // The case that failed before the shim step. A tool published ONLY as a .cmd is announced,
    // and the loader .NET uses under UseShellExecute = false could not start it by name at all.
    DirectoryInfo toolDirectory = TestTempRoot.CreateDirectory("announced-runnable-shim");
    string name = UniqueName();
    await File.WriteAllLinesAsync(Path.Combine(toolDirectory.FullName, name + ".cmd"),
        ["@echo off", "echo shim-version 1.0"], TestContext.Current.CancellationToken);
    string? previousPath = Environment.GetEnvironmentVariable("PATH");
    Environment.SetEnvironmentVariable("PATH", toolDirectory.FullName + Path.PathSeparator + previousPath);
    try
    {
      Assert.Equal([name], Announced([name]));

      ShellResult result = new ScriptGlobals(NoopRegistry(), TestTempRoot.Root, TestTempRoot.Root).Shell(name);

      Assert.True(result.ExitCode == 0,
          $"announced '{name}' did not start through Shell: exit {result.ExitCode}: {result.Stderr}");
      Assert.Equal("shim-version 1.0", result.Stdout.Trim());
    }
    finally
    {
      Environment.SetEnvironmentVariable("PATH", previousPath);
      DeleteDirectory(toolDirectory);
    }
  }

  // The other half of the same rule: an unresolvable name is never announced, so the prompt
  // cannot name a tool the spawn would refuse.
  [Fact]
  public void ANameThisMachineDoesNotResolve_IsNeverAnnounced() =>
      Assert.Empty(Announced([UniqueName()]));

  /// <summary>The subset of <paramref name="names"/> this machine would announce, resolved the
  ///     way the prompt resolves them: against the process PATH, keeping only names whose first
  ///     match is a console program or a script.</summary>
  private static IReadOnlyList<string> Announced(IReadOnlyList<string> names) =>
      [.. PathToolDiscovery
          .Resolve(names, PathDirectories())
          .Where(resolution => resolution.IsResolved)
          .Select(resolution => resolution.Name)];

  private static string[] PathDirectories() =>
      (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
          .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

  /// <summary>A name no other test in this run can collide with, so prepending a directory to the
  ///     process PATH cannot shadow anything a concurrent test resolves.</summary>
  private static string UniqueName() => "ethang-announced-" + Guid.NewGuid().ToString("N")[..10];

  private static CSharpScriptExecEngine Engine() =>
      new(CapabilityRegistry.Create([]), workspaceRoot: () => AppContext.BaseDirectory);

  private static void DeleteDirectory(DirectoryInfo directory)
  {
    // Named decision (CA1031): best-effort cleanup of a temp tree; a delete fault must not mask
    // the assertion that ran before it.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      directory.Delete(recursive: true);
    }
    catch (Exception)
#pragma warning restore CA1031 // Do not catch general exception types
    {
      // Swallowed deliberately: see the summary above.
    }
  }

  // Shell never resolves tools or invokes capabilities; an empty registry stub satisfies
  // ScriptTools' non-null contract.
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
