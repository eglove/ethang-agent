using System.Diagnostics;
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Roslyn.ACL.Tests;

public sealed class ProcessTreeGuardTests
{
  private static string PwshPath()
  {
    string candidate = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
    return File.Exists(candidate) ? candidate : "powershell.exe";
  }

  // The guard's ONLY ceiling is the process count (issue #258 removed the memory
  // ceiling: a spawned tree is an external process the harness does not own).
  [Fact]
  public void Guard_ChildUnderProcessCeiling_ReportsNoBreach()
  {
    ProcessStartInfo psi = new(PwshPath())
    {
      RedirectStandardOutput = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    psi.ArgumentList.Add("-NoLogo");
    psi.ArgumentList.Add("-NoProfile");
    psi.ArgumentList.Add("-Command");
    psi.ArgumentList.Add("Write-Output 'done'");

    using Process p = Process.Start(psi)!;
    using ProcessTreeGuard guard = new(p, processCeiling: 32, sampleInterval: TimeSpan.FromMilliseconds(100));

    _ = p.WaitForExit(TimeSpan.FromSeconds(30));

    Assert.False(guard.Outcome.ProcessCountExceeded);
  }

  // The wiring, end to end: a program that fans out past its process ceiling must
  // surface the ceiling error contract, never a bare non-zero exit the model would
  // read as the program's own failure.
  [Fact]
  public void Shell_ProgramExceedingProcessCeiling_ReportsProcessCeilingError()
  {
    ScriptGlobals globals = new(NoopRegistry(), Path.GetTempPath(), Path.GetTempPath(),
        ceilings: new ScriptCeilings(2, 256 * 1024, 8L * 1024 * 1024));

    ShellResult result = globals.Shell(PwshPath(), "-NoLogo", "-NoProfile", "-Command",
        "1..6 | ForEach-Object { Start-Process ping -ArgumentList '-n','60','127.0.0.1' -WindowStyle Hidden }; Start-Sleep -Seconds 20");

    Assert.Equal(-1, result.ExitCode);
    Assert.StartsWith("Error [ProcessCeiling]", result.Stderr, StringComparison.Ordinal);
  }

  // FINDING 1 (critical): the timer is PERIODIC and keeps firing after the kill, so a
  // post-kill sample of the now-empty tree must never clear the breach. Without the
  // terminal latch the caller's CeilingFailure() sees a clean outcome and the model
  // gets a bare non-zero exit with no ceiling error at all. This test reads the
  // outcome immediately after exit and again ~12 ticks later.
  [Fact]
  public async Task Guard_BreachObserved_IsStillReportedAfterFurtherTicks()
  {
    using Process p = StartFanOutChild();
    using ProcessTreeGuard guard = new(p, processCeiling: 2, sampleInterval: TimeSpan.FromMilliseconds(100));

    Task exit = p.WaitForExitAsync(TestContext.Current.CancellationToken);
    bool exited = await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(30),
        TestContext.Current.CancellationToken)).ConfigureAwait(true) == exit;
    ProcessTreeGuardOutcome atExit = guard.Outcome;
    // ~12 further ticks at 100 ms — the periodic timer keeps firing after the kill.
    await Task.Delay(TimeSpan.FromMilliseconds(1200), TestContext.Current.CancellationToken).ConfigureAwait(true);
    ProcessTreeGuardOutcome later = guard.Outcome;

    Assert.True(exited, "the guard must kill a tree that exceeds its process ceiling");
    Assert.True(atExit.ProcessCountExceeded, "the breach must be visible at exit");
    Assert.True(later.ProcessCountExceeded, "a breach once observed must never be cleared by a later tick");
    Assert.True(later.PeakProcesses > 2, "the peak must record the breach that killed the tree");
  }

  // FINDING 4 (important): the whole justification for the 256-process default is that
  // legitimate work must not be killed. A child that runs SECONDS (not milliseconds)
  // under the DEFAULT ceilings must exit on its own with no breach — a child that exits
  // instantly would pass vacuously even if the guard never sampled at all.
  [Fact]
  public void Guard_LegitimateWorkUnderDefaultCeilings_RunsToCompletionUnkilled()
  {
    ProcessStartInfo psi = new(PwshPath())
    {
      RedirectStandardOutput = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    psi.ArgumentList.Add("-NoLogo");
    psi.ArgumentList.Add("-NoProfile");
    psi.ArgumentList.Add("-Command");
    // Runs ~2.5 s — real work, nowhere near the default process ceiling.
    psi.ArgumentList.Add("1..20 | ForEach-Object { Start-Sleep -Milliseconds 100 }; Write-Output 'done'");

    using Process p = Process.Start(psi)!;
    using ProcessTreeGuard guard = new(p, ScriptCeilings.Default.SpawnProcessCount,
        TimeSpan.FromMilliseconds(100));

    bool exited = p.WaitForExit(TimeSpan.FromSeconds(30));

    Assert.True(exited, "legitimate work must run to completion");
    Assert.Equal(0, p.ExitCode);
    Assert.False(guard.Outcome.ProcessCountExceeded);
    Assert.True(guard.Outcome.PeakProcesses >= 1, "the guard must actually have sampled a live tree");
  }

  // FINDING 2 (important): invalid configuration THROWS at construction and names the
  // offending field — it is never clamped and never deferred to the guard's ctor where
  // Shell's catch-all would degrade it into a generic error.
  [Theory]
  [InlineData(0, "spawnProcessCount")]
  [InlineData(-5, "spawnProcessCount")]
  [InlineData(256, "outputTruncateBytes")]
  public void ScriptCeilings_InvalidConfiguration_ThrowsNamingTheField(
      int processCount, string offendingField)
  {
    ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
        () => new ScriptCeilings(processCount, 0, 0));
    Assert.Equal(offendingField, ex.ParamName);
  }

  [Fact]
  public void ScriptCeilings_NegativeOutputKillBytes_ThrowsNamingTheField()
  {
    ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
        () => new ScriptCeilings(256, 512, -1));
    Assert.Equal("outputKillBytes", ex.ParamName);
  }

  // FINDING 9 (minor): a killed program's last dying words are the most useful
  // diagnostic. The ceiling line must APPEND the program's own stderr, never replace it.
  [Fact]
  public void Shell_ProgramKilledByCeiling_PreservesItsOwnStderr()
  {
    ScriptGlobals globals = new(NoopRegistry(), Path.GetTempPath(), Path.GetTempPath(),
        ceilings: new ScriptCeilings(2, 256 * 1024, 8L * 1024 * 1024));

    // The dying words are written FIRST, then the process tree that trips the
    // ceiling is spawned: the guard kills the tree the moment the ceiling breaks, so
    // writing them afterwards raced the kill and the test failed on a loaded machine
    // (the program was killed before it had spoken).
    ShellResult result = globals.Shell(PwshPath(), "-NoLogo", "-NoProfile", "-Command",
        "[Console]::Error.WriteLine('DYING-WORDS'); "
        + "1..6 | ForEach-Object { Start-Process ping -ArgumentList '-n','60','127.0.0.1' -WindowStyle Hidden }; "
        + "Start-Sleep -Seconds 20");

    Assert.Equal(-1, result.ExitCode);
    Assert.StartsWith("Error [ProcessCeiling]", result.Stderr, StringComparison.Ordinal);
    Assert.Contains("--- program stderr ---", result.Stderr, StringComparison.Ordinal);
    Assert.Contains("DYING-WORDS", result.Stderr, StringComparison.Ordinal);
  }

  // A fan-out that trips a low process ceiling: the guard's only remaining kill reason.
  private static Process StartFanOutChild()
  {
    ProcessStartInfo psi = new(PwshPath())
    {
      RedirectStandardOutput = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    psi.ArgumentList.Add("-NoLogo");
    psi.ArgumentList.Add("-NoProfile");
    psi.ArgumentList.Add("-Command");
    psi.ArgumentList.Add("1..6 | ForEach-Object { Start-Process ping -ArgumentList '-n','60','127.0.0.1' -WindowStyle Hidden }; Start-Sleep -Seconds 30");
    return Process.Start(psi)!;
  }

  // Shell never resolves tools or invokes capabilities; an empty registry stub
  // satisfies ScriptTools' non-null contract (the ShellContainmentTests pattern).
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
