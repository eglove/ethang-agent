using System.Diagnostics;
using System.Globalization;
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Roslyn.ACL.Tests;

/// <summary>Shell containment tests (2026-10-17 incident: 4 orphaned testhosts
///     sat 6 hours / ~625 MB, 24 dotnet build servers ~2.3 GB after children ran
///     dotnet test/build via Shell). Every Shell() spawn is assigned to a
///     kill-on-close Job Object (the MCP ACL's JobObject pattern): the spawned
///     tree dies with the exec call that created it, and an app exit reaps
///     everything. The build-server question is decided here: kill-on-close for
///     ALL spawns — a killed VBCSCompiler costs one cold rebuild next call
///     (its own idle timer would have reaped it ~15 min later anyway), while a
///     surviving orphan costs gigabytes for hours; correctness over speed.
///     The detached grandchild's pid is captured through the Shell stdout
///     (Start-Process -PassThru) — the only observation window, since the
///     reaping happens inside the Shell call.</summary>
public class ShellContainmentTests
{
  // The detached-grandchild shape: the direct child (powershell) exits after
  // spawning a grandchild that outlives it — the exact orphan shape the
  // incident measured (a testhost surviving its dotnet parent). The pid of
  // the detached grandchild rides the Shell stdout.
  private const string SpawnDetachedChildAndPrintPid =
      "$p = Start-Process ping -ArgumentList '-n','60','127.0.0.1' -WindowStyle Hidden -PassThru; Write-Output $p.Id";

  [Fact]
  public async Task Shell_TreeDies_WithTheExecCall()
  {
    ScriptGlobals globals = new(NoopRegistry(), Path.GetTempPath(), Path.GetTempPath());

    ShellResult result = globals.Shell("powershell",
        "-NoProfile", "-Command", SpawnDetachedChildAndPrintPid);

    Assert.Equal(0, result.ExitCode);
    int orphanPid = int.Parse(result.Stdout.Trim(), CultureInfo.InvariantCulture);
    Process? orphan = null;
    try
    {
      orphan = Process.GetProcessById(orphanPid);
    }
    catch (ArgumentException)
    {
      // Already reaped — the assertion below still holds.
    }

    // The job closes when the Shell call returns (the Process handle is
    // disposed in the using scope), so the kernel has already reaped the
    // detached grandchild by the time this runs.
    Assert.True(orphan is null || orphan.HasExited,
        $"the detached grandchild (pid {orphanPid}) must be reaped when the Shell call returns");
    if (orphan is not null)
    {
      Task exit = orphan.WaitForExitAsync(TestContext.Current.CancellationToken);
      _ = await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(10),
          TestContext.Current.CancellationToken)).ConfigureAwait(true);
      Assert.True(orphan.HasExited,
          $"the detached grandchild (pid {orphanPid}) must be reaped when the Shell call returns");
    }
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
