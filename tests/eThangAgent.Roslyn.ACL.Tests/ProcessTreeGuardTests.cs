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

  [Fact]
  public void Guard_ChildAllocatingPastCeiling_KillsTheTreeAndReportsMemory()
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
    // Allocates ~400 MB in 8 MB chunks and holds it.
    psi.ArgumentList.Add("$a=@(); 1..50 | ForEach-Object { $a += ,(New-Object byte[] 8000000) }; Start-Sleep -Seconds 30");

    using Process p = Process.Start(psi)!;
    using ProcessTreeGuard guard = new(p, memoryCeilingBytes: 64L * 1024 * 1024,
        processCeiling: 32, sampleInterval: TimeSpan.FromMilliseconds(100));

    bool exited = p.WaitForExit(TimeSpan.FromSeconds(30));

    Assert.True(exited, "the guard must kill a tree that exceeds its memory ceiling");
    Assert.True(guard.Outcome.MemoryExceeded);
    Assert.False(guard.Outcome.ProcessCountExceeded);
  }

  [Fact]
  public void Guard_ChildStayingUnderCeiling_ReportsNoBreach()
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
    using ProcessTreeGuard guard = new(p, memoryCeilingBytes: 512L * 1024 * 1024,
        processCeiling: 32, sampleInterval: TimeSpan.FromMilliseconds(100));

    _ = p.WaitForExit(TimeSpan.FromSeconds(30));

    Assert.False(guard.Outcome.MemoryExceeded);
    Assert.False(guard.Outcome.ProcessCountExceeded);
  }

  // The wiring, end to end: a program that fans out past its process ceiling must
  // surface the ceiling error contract, never a bare non-zero exit the model would
  // read as the program's own failure. The process ceiling is used rather than the
  // memory one because the Job Object's per-process memory limit is the memory
  // backstop and can fire first; nothing backstops the process count.
  [Fact]
  public void Shell_ProgramExceedingProcessCeiling_ReportsProcessCeilingError()
  {
    ScriptGlobals globals = new(NoopRegistry(), Path.GetTempPath(), Path.GetTempPath(),
        ceilings: new ScriptCeilings(2L * 1024 * 1024 * 1024, 2, 256 * 1024, 8L * 1024 * 1024));

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
    using Process p = StartAllocatingChild();
    using ProcessTreeGuard guard = new(p, memoryCeilingBytes: 64L * 1024 * 1024,
        processCeiling: 32, sampleInterval: TimeSpan.FromMilliseconds(100));

    Task exit = p.WaitForExitAsync(TestContext.Current.CancellationToken);
    bool exited = await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(30),
        TestContext.Current.CancellationToken)).ConfigureAwait(true) == exit;
    ProcessTreeGuardOutcome atExit = guard.Outcome;
    // ~12 further ticks at 100 ms — the periodic timer keeps firing after the kill.
    await Task.Delay(TimeSpan.FromMilliseconds(1200), TestContext.Current.CancellationToken).ConfigureAwait(true);
    ProcessTreeGuardOutcome later = guard.Outcome;

    Assert.True(exited, "the guard must kill a tree that exceeds its memory ceiling");
    Assert.True(atExit.MemoryExceeded, "the breach must be visible at exit");
    Assert.True(later.MemoryExceeded, "a breach once observed must never be cleared by a later tick");
    Assert.False(later.ProcessCountExceeded);
    Assert.True(later.PeakBytes > 64L * 1024 * 1024, "the peak must record the breach that killed the tree");
    Assert.True(later.PeakProcesses >= 1, "the peak must record at least the root process");
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
    // Runs ~2.5 s and touches a few MB — real work, nowhere near either default ceiling.
    psi.ArgumentList.Add("$a=@(); 1..20 | ForEach-Object { $a += ,(New-Object byte[] 200000); Start-Sleep -Milliseconds 100 }; Write-Output 'done'");

    using Process p = Process.Start(psi)!;
    using ProcessTreeGuard guard = new(p, ScriptCeilings.Default.SpawnMemoryBytes,
        ScriptCeilings.Default.SpawnProcessCount, TimeSpan.FromMilliseconds(100));

    bool exited = p.WaitForExit(TimeSpan.FromSeconds(30));

    Assert.True(exited, "legitimate work must run to completion");
    Assert.Equal(0, p.ExitCode);
    Assert.False(guard.Outcome.MemoryExceeded);
    Assert.False(guard.Outcome.ProcessCountExceeded);
    Assert.True(guard.Outcome.PeakBytes > 0, "the guard must actually have sampled a live tree");
    Assert.True(guard.Outcome.PeakProcesses >= 1, "the guard must actually have sampled a live tree");
  }

  // FINDING 2 (important): invalid configuration THROWS at construction and names the
  // offending field — it is never clamped and never deferred to the guard's ctor where
  // Shell's catch-all would degrade it into a generic error.
  [Theory]
  [InlineData(0L, 256, "spawnMemoryBytes")]
  [InlineData(-1L, 256, "spawnMemoryBytes")]
  [InlineData(1024L, 0, "spawnProcessCount")]
  [InlineData(1024L, -5, "spawnProcessCount")]
  [InlineData(1024L, 256, "outputTruncateBytes")]
  public void ScriptCeilings_InvalidConfiguration_ThrowsNamingTheField(
      long memoryBytes, int processCount, string offendingField)
  {
    ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
        () => new ScriptCeilings(memoryBytes, processCount, 0, 0));
    Assert.Equal(offendingField, ex.ParamName);
  }

  [Fact]
  public void ScriptCeilings_NegativeOutputKillBytes_ThrowsNamingTheField()
  {
    ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
        () => new ScriptCeilings(1024L, 256, 512, -1));
    Assert.Equal("outputKillBytes", ex.ParamName);
  }

  // FINDING 9 (minor): a killed program's last dying words are the most useful
  // diagnostic. The ceiling line must APPEND the program's own stderr, never replace it.
  [Fact]
  public void Shell_ProgramKilledByCeiling_PreservesItsOwnStderr()
  {
    ScriptGlobals globals = new(NoopRegistry(), Path.GetTempPath(), Path.GetTempPath(),
        ceilings: new ScriptCeilings(2L * 1024 * 1024 * 1024, 2, 256 * 1024, 8L * 1024 * 1024));

    ShellResult result = globals.Shell(PwshPath(), "-NoLogo", "-NoProfile", "-Command",
        "1..6 | ForEach-Object { Start-Process ping -ArgumentList '-n','60','127.0.0.1' -WindowStyle Hidden }; "
        + "[Console]::Error.WriteLine('DYING-WORDS'); Start-Sleep -Seconds 20");

    Assert.Equal(-1, result.ExitCode);
    Assert.StartsWith("Error [ProcessCeiling]", result.Stderr, StringComparison.Ordinal);
    Assert.Contains("--- program stderr ---", result.Stderr, StringComparison.Ordinal);
    Assert.Contains("DYING-WORDS", result.Stderr, StringComparison.Ordinal);
  }
  private static Process StartAllocatingChild()
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
    // Allocates ~400 MB in 8 MB chunks and holds it.
    psi.ArgumentList.Add("$a=@(); 1..50 | ForEach-Object { $a += ,(New-Object byte[] 8000000) }; Start-Sleep -Seconds 30");
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
