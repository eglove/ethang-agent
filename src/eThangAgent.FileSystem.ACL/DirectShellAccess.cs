using System.Diagnostics;
using System.Text;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL;

/// <summary>
/// Runs user commands through the machine's shell via <see cref="Process"/> directly:
/// the shell IS the resolved program, invoked with an absolute path per S4036 so a
/// manipulated PATH cannot shadow it. Shell discovery probes pwsh (PATH, then the
/// standard install roots), then powershell.exe, then falls back to %COMSPEC% — the
/// first existing executable wins, resolved once per process. Commands run with
/// <c>-NoLogo -NoProfile -Command</c> (PowerShell) or <c>/d /s /c</c> (cmd fallback)
/// at the caller's working directory; PowerShell variants append
/// <c>; exit $LASTEXITCODE</c> so native exit codes propagate. stdout and stderr are
/// read concurrently and merged in arrival order, decoded as UTF-8 with no CRLF
/// rewriting. On timeout the process tree is killed and the partial output returns
/// flagged <c>TimedOut</c>; a start failure surfaces as a typed Result error.
/// </summary>
public sealed class DirectShellAccess : IShellCommandAccess
{
  private static readonly Lazy<string> ShellPath = new(ResolveShell);

  /// <summary>Test hook: the resolved shell path (production keeps the Lazy private;
  ///     tests pin resolution behavior without spawning a shell).</summary>
  internal static string ResolveShellForTests() => ShellPath.Value;

  public async Task<Result<ShellRun>> RunAsync(string workingDirectory, string command,
      TimeSpan timeout, CancellationToken ct = default)
  {
    if (string.IsNullOrWhiteSpace(command))
    {
      return Result.Failure<ShellRun>(new DomainError("EmptyCommand",
          "Command must be a non-empty string."));
    }

    string shell = ShellPath.Value;
    (string[] prefixArgs, bool isPowerShell) = ShellInvocation(shell);

    ProcessStartInfo psi = new(shell)
    {
      WorkingDirectory = workingDirectory,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
      StandardOutputEncoding = Encoding.UTF8,
      StandardErrorEncoding = Encoding.UTF8
    };
    foreach (string a in prefixArgs)
    {
      psi.ArgumentList.Add(a);
    }

    // PowerShell's exit code is the script block's success, not the last native
    // command's; the explicit exit propagates native exit codes (and keeps cmd's).
    psi.ArgumentList.Add(isPowerShell ? command + "; exit $LASTEXITCODE" : command);

    // Merged output ordering: one StringBuilder fed by both stream readers, each line
    // appended as it arrives — terminal-like interleaving instead of two blocks.
    StringBuilder merged = new();
    using SemaphoreSlim gate = new(1, 1);
    async Task AppendLineAsync(string? line)
    {
      await gate.WaitAsync(ct).ConfigureAwait(false);
      try
      {
        _ = merged.AppendLine(line);
      }
      finally
      {
        _ = gate.Release();
      }
    }

    try
    {
      using Process p = Process.Start(psi)!;
      using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
      timeoutCts.CancelAfter(timeout);

      Task stdoutTask = PumpAsync(p.StandardOutput.ReadLineAsync, AppendLineAsync, timeoutCts.Token);
      Task stderrTask = PumpAsync(p.StandardError.ReadLineAsync, AppendLineAsync, timeoutCts.Token);

      try
      {
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        await p.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        return Result.Success(new ShellRun(p.ExitCode, merged.ToString(), TimedOut: false));
      }
      catch (OperationCanceledException) when (!ct.IsCancellationRequested)
      {
        // Budget expiry (the caller's token is still live): kill the tree, drain what
        // was captured, and report the run as timed out.
        KillProcessTree(p.Id);
        return Result.Success(new ShellRun(ExitCode: -1, merged.ToString(), TimedOut: true));
      }
    }
    // Named decision (CA1031): shell transport failures (no shell, bad working dir)
    // surface as typed Result errors instead of crashes.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      return Result.Failure<ShellRun>(new DomainError("ShellStartFailed",
          $"Could not start the shell '{shell}': {ex.Message}"));
    }
#pragma warning restore CA1031 // Do not catch general exception types
  }

  /// <summary>Reads one stream line-by-line until EOF or cancellation, appending each
  ///     line through <paramref name="append"/>. ReadLineAsync(ct) returns null at EOF.
  ///     On cancellation the pump ends (the kill lands from the timeout handler).</summary>
  private static async Task PumpAsync(
      Func<CancellationToken, ValueTask<string?>> readLine,
      Func<string?, Task> append,
      CancellationToken ct)
  {
    try
    {
      while (await readLine(ct).ConfigureAwait(false) is { } line)
      {
        await append(line).ConfigureAwait(false);
      }
    }
    catch (OperationCanceledException)
    {
      // The kill path handles reporting; a cancelled pump contributes nothing.
    }
    catch (ObjectDisposedException)
    {
      // Stream torn down by process disposal mid-read after a kill.
    }
  }

  /// <summary>Absolute taskkill path (System32) per S4036.</summary>
  private static string TaskKillPath() => Path.Combine(Environment.SystemDirectory, "taskkill.exe");

  /// <summary>Kills the process and its descendants (a shell spawns children that own
  ///     pipes; killing the shell alone would leak hung children).</summary>
  private static void KillProcessTree(int processId)
  {
#pragma warning disable CA1031 // Named decision: a failed kill must not crash the run — the timeout result already carries the partial output.
    try
    {
      using Process? killer = Process.Start(new ProcessStartInfo(TaskKillPath(), $"/pid {processId} /t /f")
      {
        CreateNoWindow = true,
        UseShellExecute = false
      });
      _ = killer?.WaitForExit(5000);
    }
    catch (Exception ex)
    {
      // Best-effort kill: the shell dies with its pipes regardless, and the timeout
      // result already carries the partial output — nothing to report here.
      _ = ex;
    }
#pragma warning restore CA1031 // Named decision: a failed kill must not crash the run.
  }

  /// <summary>Shell discovery: pwsh (PATH, then standard install roots), then
  ///     powershell.exe, then %COMSPEC%. Absolute paths preferred (S4036). Resolved once
  ///     per process.</summary>
  private static string ResolveShell()
  {
    return EnumerateShellCandidates().FirstOrDefault(File.Exists)
        ?? throw new InvalidOperationException("No shell found: tried pwsh, powershell, and COMSPEC.");
  }

  private static IEnumerable<string> EnumerateShellCandidates()
  {
    string comspec = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
    string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    List<string?> candidates =
    [
      // PATH probing first: where would the user's own terminal resolve?
      WhereOnPath("pwsh.exe"),
      WhereOnPath("powershell.exe"),
      Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"),
      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
      comspec,
    ];
    return candidates
        .Where(c => !string.IsNullOrWhiteSpace(c))
        .Select(c => c!);
  }

  private static string? WhereOnPath(string exe)
  {
#pragma warning disable CA1031 // Named decision: where.exe may be absent on stripped-down systems; discovery falls through to the standard install locations.
    try
    {
      ProcessStartInfo psi = new(Path.Combine(Environment.SystemDirectory, "where.exe"), exe)
      {
        CreateNoWindow = true,
        UseShellExecute = false,
        RedirectStandardOutput = true,
      };
      using Process? p = Process.Start(psi);
      if (p is null)
      {
        return null;
      }

      string? first = p.StandardOutput.ReadLine();
      _ = p.WaitForExit(5000);
      return p.ExitCode == 0 && !string.IsNullOrWhiteSpace(first) ? first.Trim() : null;
    }
    catch (Exception ex)
    {
      _ = ex;
      return null;
    }
#pragma warning restore CA1031 // Named decision: a missing where.exe must not fail discovery.
  }

  /// <summary>Invocation shape per shell family: PowerShell takes -NoLogo -NoProfile
  ///     -Command; cmd takes /d /s /c (no profile concept, /s strips quote quirks).</summary>
  private static (string[] PrefixArgs, bool IsPowerShell) ShellInvocation(string shellPath)
  {
    string name = Path.GetFileName(shellPath);
    return name.StartsWith("pwsh", StringComparison.OrdinalIgnoreCase) || name.StartsWith("powershell", StringComparison.OrdinalIgnoreCase)
      ? (["-NoLogo", "-NoProfile", "-Command"], true)
      : (["/d", "/s", "/c"], false);
  }
}
