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
