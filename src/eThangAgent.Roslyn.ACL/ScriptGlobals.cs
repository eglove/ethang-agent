using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using eThangAgent.ToolDomain.Verification;

namespace eThangAgent.Roslyn.ACL;

/// <summary>Host object for Roslyn C# scripting. Public members are accessible as
/// top-level identifiers in the script: Workspace, Tools, Shell, Output, State.</summary>
public sealed class ScriptGlobals
{
  // Post-cancel grace for draining a killed tree's pipes and flushing its exit:
  // usually the kill closes the pipes instantly; a survivor holding one open gets
  // this long before the caller unwinds cancelled and abandons the readers.
  private static readonly TimeSpan ShellDrainGrace = TimeSpan.FromSeconds(2);

  // How often the process-tree guard counts a running program's processes (spec D10).
  // 250 ms is fast enough that a runaway fan-out is killed within a fraction of a
  // second and slow enough that the enumeration cost stays negligible against the
  // work being sampled. Memory is not sampled at all (issue #258): a spawned tree is
  // an external process the harness does not own, so its size is never a kill reason.
  private static readonly TimeSpan ShellSampleInterval = TimeSpan.FromMilliseconds(250);

  private readonly ConcurrentQueue<string> _outputLines = new();
  private int _nestedDispatchCount;
  private readonly bool _captureStdout;
  private TextWriter? _originalOut;

  /// <summary>The execution's cancellation source: caller interrupt (user stop) and the
  ///     elapsed exec budget both fire it. Synchronous members such as <see cref="Shell"/>
  ///     cannot be interrupted by Roslyn's cooperative cancellation checks, so they must
  ///     honor this token directly.</summary>
  private readonly CancellationToken _ct;

  public ScriptGlobals(ICapabilityRegistry registry, string workspace, string temp,
      bool captureStdout = true, IVerificationLedger? verificationSink = null,
      TimeSpan? execBudget = null, ScriptCeilings? ceilings = null,
      CancellationToken shellToken = default)
  {
    Workspace = workspace;
    // The resolver is built from the SAME workspace root the scripts see, so
    // PathResolver.Resolve and Path.Combine(Workspace, ...) can never disagree.
    PathResolver = new WorkspacePathResolver(workspace);
    Temp = temp;
    _captureStdout = captureStdout;
    _ct = shellToken;
    VerificationSink = verificationSink;
    ExecBudget = execBudget;
    Ceilings = ceilings ?? ScriptCeilings.Default;
    Tools = new ScriptTools(registry, this);
  }

  /// <summary>The resource ceilings every program this script spawns runs under
  ///     (spec D10). Optional with documented defaults: the exec engine and the
  ///     evidence runner both construct globals, so a required parameter would break
  ///     both.</summary>
  public ScriptCeilings Ceilings { get; }

  /// <summary>Workspace root directory.</summary>
  public string Workspace { get; }

  /// <summary>Resolves a path argument the way the FILE TOOLS do: a relative path
  ///     resolves at the WORKSPACE, an absolute path passes through, and a path that
  ///     escapes the workspace is refused (PathOutsideWorkspace).<para>The relative-path
  ///     leak (2026-10-22): a raw <c>File.WriteAllText("p1.txt", ...)</c> inside a script
  ///     resolves against the PROCESS CWD - the app's launch directory - so a session
  ///     rooted at personal-vault wrote its prompt files into the ethang-agent repo. The
  ///     engine runs scripts IN-PROCESS and concurrent sessions share that process, so
  ///     pinning the CWD per call would be a process-global race; this resolver is the
  ///     durable fix. Scripts that write files resolve their paths through it (or build
  ///     them from <see cref="Workspace"/>) so a write can never land outside the
  ///     session's own workspace.</para></summary>
  public IPathResolver PathResolver { get; }
  /// <summary>Resolves a path argument to an ABSOLUTE path at the workspace, throwing a
  ///     plain <see cref="InvalidOperationException"/> when the path is invalid or
  ///     escapes the workspace. This is the script-facing convenience over
  ///     <see cref="PathResolver"/>: a script wants a usable string, not a Result to
  ///     unpack. The Result-shaped resolver stays available for callers that want the
  ///     error contract.</summary>
  public string ResolvePath(string path)
  {
    Result<string> resolved = PathResolver.Resolve(path);
    return resolved.IsSuccess
        ? resolved.Value
        : throw new InvalidOperationException(
            $"cannot resolve path '{path}': {resolved.Error.Message}");
  }


  /// <summary>Temp directory for artifacts.</summary>
  public string Temp { get; }

  /// <summary>Optional sink receiving every completed Shell run (verification
  ///     gating reads it downstream; this surface knows nothing of that purpose).</summary>
  public IVerificationLedger? VerificationSink { get; }

  /// <summary>The enclosing exec call's stated budget, when the script runs under one
  ///     (the evidence runner constructs globals budgetless). Nested tool calls that
  ///     omit <c>timeoutSeconds</c> inherit it — the named leniency decision documented
  ///     in ExecGuide; explicit nested values still win and are still validated.</summary>
  public TimeSpan? ExecBudget { get; }

  /// <summary>How many capability dispatches this script made through Tools — the
  ///     attribution signal for empty-output runs (a discarded nested-call result
  ///     reads as empty output; the formatter uses the count to hint at it).</summary>
  public int NestedDispatchCount => _nestedDispatchCount;
  /// <summary>Counts one dispatched nested capability call. Called by
  ///     ScriptTools.Invoke past every pre-dispatch contract throw, so a
  ///     counted dispatch is one that actually reached the provider.</summary>
  internal void CountNestedDispatch() => Interlocked.Increment(ref _nestedDispatchCount);

  /// <summary>Tool-calling surface: Tools.read(...), Tools.Invoke(...), etc.</summary>
  public ScriptTools Tools { get; }

  /// <summary>Collected output lines from Output() calls and captured Console.Out.</summary>
  public IReadOnlyList<string> OutputLines => [.. _outputLines];

  /// <summary>Run an external command line and return stdout, stderr, and exit code.
  ///     Every argument after <paramref name="exe"/> is ONE native argument, passed
  ///     to the process verbatim through <see cref="ProcessStartInfo.ArgumentList"/>
  ///     (issue #95): an argument containing spaces — a path, a commit message —
  ///     reaches the target process as a single argument, and no join-and-re-split
  ///     happens anywhere. The process is spawned directly with native .NET
  ///     <see cref="Process"/> APIs — no shell intermediary — and the native exit
  ///     code propagates verbatim.</summary>
  public ShellResult Shell(string exe, params string[] args)
  {
    if (string.IsNullOrWhiteSpace(exe))
    {
      return new ShellResult(-1, "", "Shell() requires an executable.");
    }

    args ??= [];

    string[] argv = [exe, .. args];
    DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
    ProcessStartInfo psi = new()
    {
      FileName = exe,
      WorkingDirectory = Workspace,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
      StandardOutputEncoding = Encoding.UTF8,
      StandardErrorEncoding = Encoding.UTF8,
    };
    foreach (string arg in args)
    {
      psi.ArgumentList.Add(arg);
    }

    // Declared outside the try so the finally can close it on every path.
    nint job = nint.Zero;
    try
    {
      using Process p = Process.Start(psi)!;
      // THE CONTAINMENT CONTRACT (2026-10-17 incident: 4 orphaned testhosts sat 6
      // hours / ~625 MB and 24 dotnet build servers ~2.3 GB after children ran
      // dotnet test/build via Shell): the spawned tree is assigned to a
      // kill-on-close Job Object, so the tree dies when this call's Process
      // handle is disposed (the using scope) — a detached grandchild (a
      // testhost surviving its dotnet parent) is reaped with the call, and an
      // app exit reaps everything still running. Best-effort per the MCP ACL's
      // precedent: a job failure degrades to the explicit-kill path below.
      // BUILD-SERVER DECISION (named, per the task brief): kill-on-close for ALL
      // spawns, VBCSCompiler included. A killed build server costs one cold
      // rebuild next call — and its own idle timer would have reaped it ~15
      // minutes later anyway — while a surviving orphan costs gigabytes for
      // hours. Correctness over speed; a per-invocation carve-out (attach the
      // job only around dotnet test) would trust argv sniffing and leave every
      // non-test spawn uncontained.
      job = JobObject.Create();
      if (job != nint.Zero && JobObject.Configure(job))
      {
        _ = JobObject.Attach(job, p);
      }
      else
      {
        // Named decision: job failure degrades to explicit-kill disposal (the
        // MCP ACL's precedent) — containment weakens, the run still works.
        _ = JobObject.Close(job);
        job = nint.Zero;
      }
      // Spec D10: the tree is guarded against its process-count ceiling for the whole
      // run. The guard kills on breach; the outcome decides the error contract the
      // model sees, so a killed program is never mistaken for a clean exit. Memory is
      // deliberately NOT a ceiling (issue #258): a spawned tree is an external process
      // the harness does not own, so a legitimate build or test run must never be
      // killed for growing.
      using ProcessTreeGuard guard = new(p, Ceilings.SpawnProcessCount, ShellSampleInterval);
      // Spec D10: both pipes feed ONE ceiling, so the bound is on the run's total
      // output rather than per stream. Exceeding the kill cap kills the tree and
      // reports the ceiling; past the truncate cap the head survives with an explicit
      // marker, so a clipped result is never mistaken for a short one.
      OutputCeiling ceiling = new(Ceilings.OutputTruncateBytes, Ceilings.OutputKillBytes);
      Task stdoutTask = PumpAsync(p.StandardOutput, ceiling, isErrorStream: false, () => KillTree(p));
      Task stderrTask = PumpAsync(p.StandardError, ceiling, isErrorStream: true, () => KillTree(p));
      // Kill the whole tree when the exec budget elapses or the turn is stopped:
      // a synchronous Shell cannot observe the token any other way. Killing closes
      // the pipes, which completes the drain tasks.
      using CancellationTokenRegistration reg = _ct.Register(() => KillTree(p));
      try
      {
        Task.WaitAll([stdoutTask, stderrTask], _ct);
      }
      catch (OperationCanceledException)
      {
        // Expected when the budget elapses or the turn is stopped.
      }

      UnwindIfCancelled(p, stdoutTask, stderrTask);

      p.WaitForExit(); // flushes output handlers so the exit code is final

      // A breach is the run's outcome, not a footnote: the model must see WHY the
      // program stopped, never a bare non-zero exit it would read as the program's
      // own failure. The output ceiling is checked FIRST: it is the reason the tree
      // was killed, and a kill also empties the process tree the guard samples.
      if (OutcomeFailure(guard, ceiling) is { } failure)
      {
        ReportRun(argv, -1, startedUtc);
        return failure;
      }

      ReportRun(argv, p.ExitCode, startedUtc);
      return new ShellResult(p.ExitCode, ceiling.Render(), ceiling.RenderStderr());
    }
    catch (OperationCanceledException)
    {
      throw; // never swallowed by the generic handler below
    }
    // Named decision (CA1031): script-invoked commands must never crash the agent turn —
    // any spawn/drain fault surfaces as a failed ShellResult the model can read.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception ex)
    {
      ReportRun(argv, -1, startedUtc);
      return new ShellResult(-1, "", ex.Message);
    }
#pragma warning restore CA1031 // Do not catch general exception types
    finally
    {
      // Closing the kill-on-close job reaps any tree member that outlived the
      // direct child (the detached-grandchild shape) — on the success, failure,
      // and cancellation paths alike. The job handle closing is also the
      // kernel's reaping path when THIS process dies: every Shell tree is
      // contained for the app's lifetime, not just this call's.
      _ = JobObject.Close(job);
    }
  }

  /// <summary>Killing the tree closes its pipes, so the drain tasks finish promptly;
  ///     the wait above can still return cleanly if they beat the token. Check the
  ///     token explicitly and unwind: the engine maps this to Cancelled/Timeout — a
  ///     stopped turn must never surface as a successful shell result.
  ///     <para>The drain is bounded: killing the tree usually closes its pipes, but a
  ///     survivor (a detached child cmd /c start launched) can hold a pipe open
  ///     forever. Named decision (S8949): no token-aware wait — the token has already
  ///     fired — but an unbounded drain let a surviving orphan wedge the caller (the
  ///     stop-button hang). Wait out a short grace for a real flush; past it, abandon
  ///     the drain readers (their buffers finalize with the pipe handles) and the
  ///     exit-code flush, and unwind cancelled. A hung stop must settle, not leak the
  ///     turn.</para></summary>
  private void UnwindIfCancelled(Process process, Task stdoutTask, Task stderrTask)
  {
    if (!_ct.IsCancellationRequested)
    {
      return;
    }

    _ = Task.WaitAll([stdoutTask, stderrTask], ShellDrainGrace);
    _ = process.WaitForExit(ShellDrainGrace);
    throw new OperationCanceledException(_ct);
  }

  /// <summary>Pumps one pipe into the shared <see cref="OutputCeiling"/> until the pipe
  ///     closes or the run must be stopped. On a kill-cap breach the tree is killed
  ///     (closing both pipes, which completes the sibling pump) and the pump returns.
  ///     <para>Reads take CancellationToken.None by the same named decision (S8949) the
  ///     drains carried: cancellation is enforced by the kill registration, never by
  ///     faulting a read the cancellation path still depends on.</para></summary>
  private static async Task PumpAsync(StreamReader reader, OutputCeiling ceiling,
      bool isErrorStream, Action onExceeded)
  {
    char[] buffer = new char[8192];
    while (true)
    {
      int read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false);
      if (read == 0)
      {
        return;
      }

      if (!ceiling.Append(new string(buffer, 0, read), isErrorStream))
      {
        onExceeded();
        return;
      }
    }
  }

  /// <summary>Kills a runaway program's whole tree. Named decision (CA1031): the process
  ///     may already have exited; the ceiling result is what the model reads, so a
  ///     failed kill is not a second failure to report.</summary>
  private static void KillTree(Process process)
  {
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      process.Kill(entireProcessTree: true);
    }
    catch (Exception)
    {
      // already exited
    }
#pragma warning restore CA1031 // Do not catch general exception types
  }

  /// <summary>The run's ceiling outcome, or null when it stayed inside every ceiling.
  ///     The output ceiling is checked FIRST: it is the reason the tree was killed, and
  ///     a kill also empties the process tree the guard samples.</summary>
  private ShellResult? OutcomeFailure(ProcessTreeGuard guard, OutputCeiling ceiling) =>
      ceiling.Exceeded
          ? OutputCeilingFailure(ceiling)
          : CeilingFailure(guard.Outcome, ceiling.Render(), ceiling.RenderStderr());

  /// <summary>The output-ceiling error contract. The head the run managed to produce is
  ///     still returned as stdout — a runaway's opening lines are often the only
  ///     diagnostic — and the marker inside it names the limit. Exit code -1 (never the
  ///     program's own) so a stopped run can never be mistaken for a clean exit.</summary>
  private ShellResult OutputCeilingFailure(OutputCeiling ceiling) => new(-1, ceiling.Render(),
      WithProgramStderr(
          "Error [OutputCeiling]: the program produced more than "
          + OutputCeiling.DescribeCap(Ceilings.OutputKillBytes)
          + " of output and was stopped.",
          ceiling.RenderStderr()));

  /// <summary>Translates a guard breach into the error contract the model reads, or
  ///     null when the run stayed inside its ceilings. The exit code is -1 (never the
  ///     program's own) so a killed program can never be mistaken for a clean exit.
  ///     <para>Both streams collected before the kill are PRESERVED: stdout as the
  ///     output ceiling captured it (verbatim inside the caps, marked when clipped),
  ///     and the program's own stderr APPENDED after the ceiling line rather than
  ///     replaced — for a killed program its last dying words are the most useful
  ///     diagnostic, and discarding them would leave the model with a bare verdict.</para></summary>
  private ShellResult? CeilingFailure(ProcessTreeGuardOutcome outcome, string stdout, string stderr) =>
      outcome.ProcessCountExceeded
          ? new ShellResult(-1, stdout, WithProgramStderr(
              "Error [ProcessCeiling]: the program exceeded its " + Ceilings.SpawnProcessCount
              + " process ceiling and was stopped.", stderr))
          : null;

  /// <summary>The ceiling line with the program's own stderr appended after it, so a
  ///     killed program's dying words survive the verdict. An empty stream adds
  ///     nothing.</summary>
  private static string WithProgramStderr(string ceilingLine, string programStderr) =>
      string.IsNullOrWhiteSpace(programStderr)
          ? ceilingLine
          : ceilingLine + "\n--- program stderr ---\n" + programStderr;

  /// <summary>Reports one completed run to the optional verification sink.
  ///     A sink fault is swallowed by named decision (spec error rule): the
  ///     report path may never break the command run being reported.</summary>
  private void ReportRun(IReadOnlyList<string> argv, int exitCode, DateTimeOffset startedUtc)
  {
    if (VerificationSink is null)
    {
      return;
    }

    // Named decision (CA1031): see summary - under-reporting degrades to more
    // nudges, never to a broken run.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      VerificationSink.Append(new ShellExecutionRecord(argv, exitCode, startedUtc, DateTimeOffset.UtcNow));
    }
    catch
    {
      // Report failures are contractually silent.
    }
#pragma warning restore CA1031 // Do not catch general exception types
  }

  /// <summary>Returns the last <paramref name="chars"/> characters of text (or the
  ///     whole string when it is shorter). Convenience for bounded output of shell results:
  ///     Output(Tail(r.Stdout, 2000)) never throws on short output, unlike r.Stdout[^N..].</summary>
  public static string Tail(string? text, int chars)
  {
    if (text is null || chars <= 0)
    {
      return "";
    }

    bool bounded = text.Length <= chars;
    return bounded ? text : text[^chars..];
  }
  /// <summary>Append a line to the script output. Does not terminate the script.
  /// Also capture any text written to Console.Out.</summary>
  public void Output(object? value)
  {
    // The projection (ScriptValueJson) is what keeps the collectible
    // submission assembly reclaimable: System.Text.Json must never see a type
    // declared in the submission - it would cache that type in a static
    // options singleton and pin the whole context for the process lifetime
    // (the 2026-10-19 leak: 15 GB working set, 9.3M handles).
    string text = value is null ? "" : ScriptValueJson.Serialize(value);
    _outputLines.Enqueue(text);
  }

  /// <summary>Redirect Console.Out to capture writes like Console.WriteLine().
  /// Called before script execution, restored after.</summary>
  public void BeginCapture()
  {
    if (_captureStdout)
    {
      _originalOut = Console.Out;
      // Named decision (CA2000): ownership transfers to Console.Out; EndCapture restores it.
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
      Console.SetOut(new CapturingWriter(this));
#pragma warning restore CA2000 // Call IDisposable.Dispose on object created by
    }
  }

  public void EndCapture()
  {
    if (_originalOut is not null)
    {
      Console.SetOut(_originalOut);
      _originalOut = null;
    }
  }

  private sealed class CapturingWriter(ScriptGlobals globals) : TextWriter
  {
    public override Encoding Encoding => Encoding.UTF8;
    public override void Write(char value) => globals._outputLines.Enqueue(value.ToString());
    public override void Write(string? value)
    {
      if (value is not null)
      {
        globals._outputLines.Enqueue(value);
      }
    }
    public override void WriteLine(string? value)
    {
      if (value is not null)
      {
        globals._outputLines.Enqueue(value);
      }
      else
      {
        globals._outputLines.Enqueue("");
      }
    }
  }
}

/// <summary>Result of Shell() call.</summary>
public sealed record ShellResult(int ExitCode, string Stdout, string Stderr);

/// <summary>The resource ceilings one script's spawned programs run under (spec D10).
///     Optional on <see cref="ScriptGlobals"/> with these documented defaults, so
///     every existing construction site keeps working; a caller that has a
///     machine-derived budget (the session's <c>ResourceBudget</c>) passes its own.
///     <para><see cref="SpawnProcessCount"/> is a hard cap, not a tuned one: a
///     measured full-solution <c>dotnet build</c> tree peaks at 40 processes and a
///     <c>dotnet test</c> run fans out one testhost per test module (24 here) on top of
///     the build servers, so the ceiling must sit well above legitimate fan-out or it
///     would kill honest work. 256 leaves that headroom while still stopping the
///     runaway shape it exists for — a program spawning hundreds or thousands of
///     processes.</para>
///     <para>There is deliberately NO memory ceiling (issue #258): a spawned program is
///     an external process the harness does not own, so its working set is never a
///     reason to kill it — a legitimate build or test run must never be mistaken for a
///     runaway. Memory is observed elsewhere (the app's own RSS monitor), which enacts
///     nothing.</para>
///     <para>The output ceilings belong to the same per-program budget and bound the
///     captured output of every Shell run: past the truncate cap the head survives with
///     an explicit marker, past the kill cap the run is stopped.</para></summary>
public sealed record ScriptCeilings
{
  /// <summary>A ceiling record. Every value is validated HERE, at construction, and an
  ///     invalid one THROWS naming the offending field — never clamped, and never
  ///     deferred to the guard's constructor where the caller's catch-all would degrade
  ///     it into a generic error the model cannot act on.</summary>
  public ScriptCeilings(int spawnProcessCount, long outputTruncateBytes, long outputKillBytes)
  {
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(spawnProcessCount);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputTruncateBytes);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputKillBytes);
    // The two output caps are ORDERED, not independent: a kill cap at or below the
    // truncate cap stops the run at the very moment the head fills, so the head renders
    // with NO marker and only the error line keeps the result from being silent — the
    // exact silent truncation the ceiling exists to prevent. Validated here, at
    // construction, naming the offending field (never clamped, never deferred to the
    // ceiling where Shell's catch-all would degrade it into an unactionable error).
    if (outputKillBytes <= outputTruncateBytes)
    {
      throw new ArgumentOutOfRangeException(nameof(outputKillBytes), outputKillBytes,
          "outputKillBytes must be greater than outputTruncateBytes ("
          + outputTruncateBytes.ToString(CultureInfo.InvariantCulture)
          + "); otherwise the run is stopped the moment the head fills and the truncation"
          + " marker can never be rendered.");
    }

    SpawnProcessCount = spawnProcessCount;
    OutputTruncateBytes = outputTruncateBytes;
    OutputKillBytes = outputKillBytes;
  }

  /// <summary>The per-program process-count ceiling: a program whose tree exceeds it is
  ///     killed.</summary>
  public int SpawnProcessCount { get; }

  /// <summary>The byte count at which a program's captured output is truncated: the head
  ///     is kept and an explicit marker names the limit and the recovery, so a clipped
  ///     result is never mistaken for a short one. BOTH pipes feed one ceiling, so this
  ///     bounds the run's TOTAL output rather than each stream.</summary>
  public long OutputTruncateBytes { get; }

  /// <summary>The byte count at which a program's output is treated as a runaway and the
  ///     program stopped (exit -1, <c>Error [OutputCeiling]</c>). Sits above the
  ///     truncate cap: between the two, output is clipped but the run continues.</summary>
  public long OutputKillBytes { get; }

  /// <summary>The documented default ceilings. Processes: 256 per tree (a MEASURED
  ///     full-solution build peaks at 40; the <c>dotnet test</c> fan-out of one testhost
  ///     per module is INFERRED from the runner's documented behavior, not measured
  ///     here — 256 sits above both). Output: truncate at 256 KB, kill at 8 MB.</summary>
  public static ScriptCeilings Default { get; } =
      new(256, 256 * 1024, 8L * 1024 * 1024);
}

/// <summary>Tool-calling surface exposed to C# scripts as Tools.read(...) etc.
/// Executing scripts call these directly; the full action set (including names like
/// "state.set" that are not identifiers) is reachable through <see cref="Invoke"/>.
/// available for actions whose names aren't valid C# identifiers.</summary>
// Named decision (CA1707): convenience methods deliberately mirror the wire tool names
// (git_status, git_commit, ...) — they are the model-facing script API, so underscores
// are the contract, not a style violation.
#pragma warning disable CA1707 // Identifiers should not contain underscores
public sealed class ScriptTools
{
  private readonly ICapabilityRegistry _registry;
  private readonly ScriptGlobals _globals;

  public ScriptTools(ICapabilityRegistry registry, ScriptGlobals globals)
  {
    ArgumentNullException.ThrowIfNull(registry);
    ArgumentNullException.ThrowIfNull(globals);
    _registry = registry;
    _globals = globals;
  }

  /// <summary>Invoke a tool by name and return the raw tool result text.
  ///     The execution budget resolves as: an explicit timeoutSeconds (whole seconds,
  ///     1..3600) on the nested arguments, else — the named leniency decision — the
  ///     enclosing exec call's budget (<see cref="ScriptGlobals.ExecBudget"/>) when the
  ///     script runs under one; only budgetless globals (the evidence runner) demand
  ///     an explicit nested value. The budget is validated here and STRIPPED from the
  ///     arguments before dispatch (an inherited value is INJECTED for tools whose
  ///     contract declares it) so providers never see a harness-reserved key they did
  ///     not ask for. Pre-dispatch contract violations — missing or invalid budget,
  ///     unknown action, malformed arguments — THROW <see cref="ScriptToolException"/>
  ///     instead of returning an error string: batch scripts routinely discard result
  ///     strings, and an in-band error buried among successes silently loses work. The
  ///     exec engine surfaces the throw as Error [ScriptError] alongside whatever
  ///     Output() evidence was already collected. Post-dispatch outcomes stay in-band:
  ///     tool-level errors (the tool's own Error [Code]: text) and elapsed budgets
  ///     (Error [ToolTimeout], enforced by the harness for EVERY action — TimeoutPolicy
  ///     no longer exempts nested calls; on the wire SelfManaged still means the tool
  ///     validates its own envelope).</summary>
  public string Invoke(string name, object? args)
  {
    ResolvedCapability resolved = ResolveOrThrow(name);
    string json = SerializeArguments(args);
    JsonElement document = ParseArguments(json);
    (TimeSpan budget, bool explicitBudget) = ResolveBudget(document, name);

    // Counted here, past every pre-dispatch contract throw: a counted dispatch is one
    // that actually reached the provider. The empty-output hint keys off this count.
    _globals.CountNestedDispatch();

    bool declaresTimeout = resolved.Action.Parameters
        .Any(pr => pr.Name == ToolTimeout.ParameterName);
    string stripped = PrepareArguments(document, json, budget, explicitBudget, declaresTimeout);

    CapabilityInvocationResult result = Dispatch(name, resolved, stripped, budget);
    if (result.IsError && IsContractError(result.Content))
    {
      // Argument-shape violations have no legitimate continue-path: the script cannot
      // branch its way out of a malformed call, so an in-band error here is exactly the
      // buried-failure hazard the pre-dispatch throws already prevent. The nested-call
      // attribution is inserted after the Error [CODE]: prefix so the wire contract holds.
      throw new ScriptToolException(NestError(name, result.Content));
    }

    return result.Content;
  }

  /// <summary>Resolves the capability by name, throwing the UnknownAction contract
  ///     error when the registry has no such action.</summary>
  private ResolvedCapability ResolveOrThrow(string name)
  {
    Result<ResolvedCapability> resolved = _registry.Resolve(name);
    return resolved.IsSuccess
        ? resolved.Value
        : throw new ScriptToolException($"Error [UnknownAction]: {resolved.Error.Message}");
  }

  /// <summary>Serializes the arguments to JSON: a string passes through verbatim,
  ///     null becomes the empty object, anything else is JSON-serialized.
  ///     The projection (ScriptValueJson) is what keeps the collectible
  ///     submission assembly reclaimable: a script that passes an anonymous
  ///     object to a tool (Tools.Invoke("read", new { path = "x" })) hands
  ///     System.Text.Json a type declared in the submission - and the serializer
  ///     would cache it in a static options singleton, pinning the collectible
  ///     context for the process lifetime (the 2026-10-19 leak: 15 GB working
  ///     set, 9.3M handles).</summary>
  private static string SerializeArguments(object? args) => args switch
  {
    null => "{}",
    string s => s,
    _ => ScriptValueJson.Serialize(args)
  };

  /// <summary>Parses the serialized arguments, throwing the InvalidJsonArguments
  ///     contract error on malformed JSON or a non-object document.</summary>
  private static JsonElement ParseArguments(string json)
  {
    JsonElement document;
    try
    {
      using JsonDocument parsed = JsonDocument.Parse(json);
      document = parsed.RootElement.Clone();
    }
    catch (JsonException ex)
    {
      throw new ScriptToolException($"Error [InvalidJsonArguments]: Arguments are not valid JSON: {ex.Message}");
    }

    return document.ValueKind != JsonValueKind.Object
        ? throw new ScriptToolException("Error [InvalidJsonArguments]: Arguments must be a JSON object.")
        : document;
  }

  /// <summary>Resolves the nested call's execution budget: an explicit timeoutSeconds
  ///     wins; otherwise the enclosing exec call's budget is inherited (the named
  ///     leniency decision); a budgetless caller with no explicit value, or any
  ///     invalid explicit value, throws the corresponding contract error.</summary>
  private (TimeSpan Budget, bool Explicit) ResolveBudget(JsonElement document, string name)
  {
    Result<TimeSpan> parsedBudget = ToolTimeout.Parse(document);
    if (parsedBudget.IsSuccess)
    {
      return (parsedBudget.Value, true);
    }

    if (parsedBudget.Error.Code == "MissingParameter" && _globals.ExecBudget is { } inherited)
    {
      return (inherited, false);
    }

    if (parsedBudget.Error.Code == "MissingParameter")
    {
      throw new ScriptToolException(
          $"Error [MissingParameter]: nested call '{name}': {parsedBudget.Error.Message} (no enclosing exec budget to inherit)");
    }

    throw new ScriptToolException(
        $"Error [{parsedBudget.Error.Code}]: nested call '{name}': {parsedBudget.Error.Message}");
  }

  /// <summary>Produces the dispatched arguments: tools whose contract declares
  ///     timeoutSeconds (ITool-backed actions) re-validate it themselves — they need
  ///     it present: an explicit nested value passes through untouched, an inherited
  ///     one is injected. Pure capability providers never declare it; strip the
  ///     harness-reserved key so their strict parsers accept the rest.</summary>
  private static string PrepareArguments(
      JsonElement document, string json, TimeSpan budget, bool explicitBudget, bool declaresTimeout)
  {
    if (declaresTimeout)
    {
      // Explicit budget: pass through. Inherited: inject it so the tool's own
      // strict parser sees the key its contract declares.
      return explicitBudget ? json : InjectTimeout(document, budget);
    }

    return StripTimeout(document);
  }

  /// <summary>Runs the dispatched invocation to completion: the registry call is
  ///     scheduled off the caller's synchronization context (see the comment inside)
  ///     and raced against a hard deadline timer, then awaited synchronously.</summary>
  private CapabilityInvocationResult Dispatch(string name, ResolvedCapability resolved, string stripped, TimeSpan budget)
  {
    // Offload to the worker pool before blocking: scripts are synchronous but the
    // registry is async, and awaiting inline deadlocks whenever this runs on a thread
    // whose SynchronizationContext must pump (e.g. Avalonia's UI thread). The caller's
    // SynchronizationContext is shed INSIDE the delegate — continuations resume on the
    // pool, never on a blocked pump — while ExecutionContext (AsyncLocal ambient state
    // such as SubAgentSpawner.RunningChild) still FLOWS: suppressing it broke
    // agent.spawn parent resolution from inside exec scripts (2026-10-04 incident).
    Task<CapabilityInvocationResult> scheduled = Task.Run(async () =>
    {
      SynchronizationContext? callerContext = SynchronizationContext.Current;
      SynchronizationContext.SetSynchronizationContext(null);
      try
      {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        // The stated budget bounds EVERY nested action, SelfManaged or not: a hung
        // nested call must not hang the script. Cooperative cancellation (cts) stops
        // tools that honor the token; the hard deadline below guarantees the bound
        // even for a provider that ignores it — in-process work cannot be killed, so
        // that invocation is abandoned and its result discarded.
        cts.CancelAfter(budget);
        Task<CapabilityInvocationResult> call = _registry.InvokeAsync(resolved, stripped, cts.Token);
        TaskCompletionSource deadline = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using Timer timer = new(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            deadline, budget, Timeout.InfiniteTimeSpan);
        Task winner = await Task.WhenAny(call, deadline.Task).ConfigureAwait(false);
        if (winner != call)
        {
          // Observe and drop the abandoned call's eventual fault so it cannot surface
          // as an unobserved task exception later in the process lifetime.
          _ = call.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
          return TimedOut(name, budget);
        }

        try
        {
          return await call.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
          return TimedOut(name, budget);
        }
      }
      finally
      {
        SynchronizationContext.SetSynchronizationContext(callerContext);
      }
    });

    return scheduled.GetAwaiter().GetResult();
  }

  /// <summary>The in-band timeout outcome for a nested call the deadline (or the
  ///     cooperative cancellation) retired.</summary>
  private static CapabilityInvocationResult TimedOut(string name, TimeSpan budget) =>
      new(ToolTimeout.TimedOut(name, budget).Content, true);
  /// <summary>Inserts "nested call '<paramref name="name"/>'": " after the leading
  ///     "Error [CODE]: " prefix (or prepends it when the content carries no such
  ///     prefix), so contract violations thrown from a script name the inner
  ///     invocation rather than reading as an exec-envelope failure.</summary>
  private static string NestError(string name, string content)
  {
    int prefixEnd = content.IndexOf("]: ", StringComparison.Ordinal);
    return prefixEnd < 0
        ? $"nested call '{name}': {content}"
        : $"{content[..(prefixEnd + 3)]}nested call '{name}': {content[(prefixEnd + 3)..]}";
  }
  /// <summary>Codes that identify an argument-shape contract violation: knowable from
  ///     the arguments alone, before any state changed. These throw from Invoke;
  ///     environmental outcomes (FileNotFound, AnchorNotFound, budgets, ...) stay in-band.</summary>
  private static readonly HashSet<string> ContractErrorCodes = new(StringComparer.Ordinal)
  {
    "InvalidParameterValue",
    "MissingParameter",
  };

  /// <summary>True when the in-band error result carries one of the codes that mark an
  ///     argument-shape contract violation rather than an environmental outcome.</summary>
  private static bool IsContractError(string resultContent) =>
      ContractErrorCodes.Any(code => resultContent.StartsWith($"Error [{code}]", StringComparison.Ordinal));

  /// <summary>Throws when <paramref name="result"/> is an in-band error result
  ///     (starts with "Error ["); otherwise returns it unchanged. The batch-script
  ///     escape hatch for environmental errors that must not be swallowed.</summary>
  public static string Require(string result)
  {
    ArgumentNullException.ThrowIfNull(result);
    return result.StartsWith("Error [", StringComparison.Ordinal)
        ? throw new ScriptToolException(result)
        : result;
  }

  /// <summary>Serializes the argument object minus the harness-reserved timeoutSeconds key.</summary>
  private static string StripTimeout(JsonElement document)
  {
    MemoryStream buffer = new();
    using (Utf8JsonWriter writer = new(buffer))
    {
      writer.WriteStartObject();
      foreach (JsonProperty property in document.EnumerateObject())
      {
        if (property.Name == ToolTimeout.ParameterName)
        {
          continue;
        }

        property.WriteTo(writer);
      }
      writer.WriteEndObject();
    }
    return Encoding.UTF8.GetString(buffer.ToArray());
  }

  /// <summary>Serializes the argument object with the inherited exec budget injected as
  ///     the timeoutSeconds key: tools whose contract declares the parameter re-validate
  ///     it themselves, so an inherited (not explicitly stated) budget must still reach
  ///     them in the dispatched arguments.</summary>
  private static string InjectTimeout(JsonElement document, TimeSpan budget)
  {
    MemoryStream buffer = new();
    using (Utf8JsonWriter writer = new(buffer))
    {
      writer.WriteStartObject();
      foreach (JsonProperty property in document.EnumerateObject())
      {
        if (property.Name == ToolTimeout.ParameterName)
        {
          continue;
        }

        property.WriteTo(writer);
      }
      writer.WriteNumber(ToolTimeout.ParameterName,
          (int)Math.Round(budget.TotalSeconds, MidpointRounding.ToZero));
      writer.WriteEndObject();
    }
    return Encoding.UTF8.GetString(buffer.ToArray());
  }

  /// <summary>Convenience one-liners for the ITool-backed actions whose names are valid
  ///     C# identifiers — the full catalog is also reachable through Invoke (state.*,
  ///     agent.*, plan.*, and memory.* stay Invoke-only: dots and hyphens do not bind).
  ///     Every wrapper is Invoke(name, args) verbatim; nothing dispatches differently.</summary>
  public string read(object? args = null) => Invoke("read", args);
  public string write(object? args = null) => Invoke("write", args);
  public string write_markdown(object? args = null) => Invoke("write_markdown", args);
  public string edit(object? args = null) => Invoke("edit", args);
  public string exec(object? args = null) => Invoke("exec", args);
  public string git_status(object? args = null) => Invoke("git_status", args);
  public string working_diff(object? args = null) => Invoke("working_diff", args);
  public string git_commit(object? args = null) => Invoke("git_commit", args);
  public string worktree(object? args = null) => Invoke("worktree", args);
  public string command_output(object? args = null) => Invoke("command_output", args);
  public string web_fetch(object? args = null) => Invoke("web_fetch", args);
  public string db_schema(object? args = null) => Invoke("db_schema", args);
  public string db_query(object? args = null) => Invoke("db_query", args);
  public string sqlite_query(object? args = null) => Invoke("sqlite_query", args);
  public string todo(object? args = null) => Invoke("todo", args);
  public string context_edit(object? args = null) => Invoke("context_edit", args);
  public string cycle_check(object? args = null) => Invoke("cycle_check", args);
  public string skill_list(object? args = null) => Invoke("skill_list", args);
  public string skill_view(object? args = null) => Invoke("skill_view", args);
  public string skill_search(object? args = null) => Invoke("skill_search", args);
  public string skill_invoke(object? args = null) => Invoke("skill_invoke", args);
  public string skill_manage(object? args = null) => Invoke("skill_manage", args);
  public string skill_registry(object? args = null) => Invoke("skill_registry", args);

  public string List() => string.Join("\n", _registry.Providers.SelectMany(p => p.Actions)
      .Select(a => $"{a.Name}({string.Join(", ", a.Parameters.Select(p => $"{p.Name}: {p.Type}"))})"));

  public string Describe(string name)
  {
    Result<ResolvedCapability> resolved = _registry.Resolve(name);
    if (!resolved.IsSuccess)
    {
      return $"Error [UnknownAction]: {resolved.Error.Message}";
    }

    ActionDescriptor action = resolved.Value.Action;
    StringBuilder sb = new($"{action.Name} — {action.Summary}\n\n{action.Description}");
    foreach (ActionParameter p in action.Parameters)
    {
      _ = sb.Append(CultureInfo.InvariantCulture, $"\n- {p.Name}: {p.Type} — {p.Description}");
    }

    return sb.ToString();
  }
}
#pragma warning restore CA1707 // Identifiers should not contain underscores
