using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Roslyn.ACL;

/// <summary>Host object for Roslyn C# scripting. Public members are accessible as
/// top-level identifiers in the script: Workspace, Tools, Shell, Output, State.</summary>
public sealed class ScriptGlobals
{
  private readonly ConcurrentQueue<string> _outputLines = new();
  private readonly bool _captureStdout;
  private TextWriter? _originalOut;

  /// <summary>The execution's cancellation source: caller interrupt (user stop) and the
  ///     elapsed exec budget both fire it. Synchronous members such as <see cref="Shell"/>
  ///     cannot be interrupted by Roslyn's cooperative cancellation checks, so they must
  ///     honor this token directly.</summary>
  private readonly CancellationToken _ct;

  public ScriptGlobals(ICapabilityRegistry registry, string workspace, string temp,
      bool captureStdout = true, CancellationToken shellToken = default)
  {
    Workspace = workspace;
    Temp = temp;
    _captureStdout = captureStdout;
    _ct = shellToken;
    Tools = new ScriptTools(registry, this);
  }

  /// <summary>Workspace root directory.</summary>
  public string Workspace { get; }

  /// <summary>Temp directory for artifacts.</summary>
  public string Temp { get; }

  /// <summary>Tool-calling surface: Tools.read(...), Tools.Invoke(...), etc.</summary>
  public ScriptTools Tools { get; }

  /// <summary>Collected output lines from Output() calls and captured Console.Out.</summary>
  public IReadOnlyList<string> OutputLines => [.. _outputLines];

  /// <summary>Run an external command line and return stdout, stderr, and exit code.
  ///     Every argument after <paramref name="exe"/> is one token of a single native
  ///     command line: the joined line is re-parsed into argv tokens with Windows
  ///     CommandLineToArgvW semantics (<see cref="NativeCommandLine.Split"/>), so a
  ///     multi-token piece such as "build -c Release" reaches the exe as separate
  ///     arguments instead of one quoted literal. The process is spawned directly
  ///     with native .NET <see cref="Process"/> APIs — no shell intermediary — and
  ///     the native exit code propagates verbatim.</summary>
  public ShellResult Shell(string exe, params string[] args)
  {
    string commandLine = string.Join(" ", new[] { exe }.Concat(args));
    IReadOnlyList<string> tokens = NativeCommandLine.Split(commandLine);
    if (tokens.Count == 0)
    {
      return new ShellResult(-1, "", "Shell() requires an executable.");
    }

    ProcessStartInfo psi = new()
    {
      FileName = tokens[0],
      WorkingDirectory = Workspace,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
      StandardOutputEncoding = Encoding.UTF8,
      StandardErrorEncoding = Encoding.UTF8,
    };
    for (int i = 1; i < tokens.Count; i++)
    {
      psi.ArgumentList.Add(tokens[i]);
    }

    try
    {
      using Process p = Process.Start(psi)!;
      // Drain both pipes concurrently: sequential ReadToEnd calls deadlock when the
      // child fills the pipe that is not being read (chatty stderr under quiet stdout).
      // Named decision (S8949): the reads take CancellationToken.None — cancellation is
      // enforced by the kill registration below (killing closes the pipes, completing
      // the drains); a cancelled mid-read would fault the unconditional drain the
      // cancellation path relies on.
      Task<string> stdoutTask = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
      Task<string> stderrTask = p.StandardError.ReadToEndAsync(CancellationToken.None);
      // Kill the whole tree when the exec budget elapses or the turn is stopped:
      // a synchronous Shell cannot observe the token any other way. Killing closes
      // the pipes, which completes the drain tasks.
      using CancellationTokenRegistration reg = _ct.Register(() =>
      {
        try
        {
          p.Kill(entireProcessTree: true);
        }
        // Named decision (CA1031): kill races are expected; the process may already
        // have exited. Swallowing is the entire point of this guard.
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception) { /* already exited */ }
#pragma warning restore CA1031 // Do not catch general exception types
      });
      try
      {
        Task.WaitAll([stdoutTask, stderrTask], _ct);
      }
      catch (OperationCanceledException)
      {
        // Expected when the budget elapses or the turn is stopped.
      }

      // Killing the tree closes its pipes, so the drain tasks finish promptly; the
      // WaitAll above can still return cleanly if they beat the token. Check the
      // token explicitly and unwind: the engine maps this to Cancelled/Timeout —
      // a stopped turn must never surface as a successful shell result.
      if (_ct.IsCancellationRequested)
      {
        // Named decision (S8949): this wait must complete unconditionally — the token
        // has already fired, and a token-aware WaitAll would throw without draining.
        Task.WaitAll([stdoutTask, stderrTask], CancellationToken.None);
        p.WaitForExit(); // flushes output handlers before the tree dies fully
        throw new OperationCanceledException(_ct);
      }

      p.WaitForExit(); // flushes output handlers so the exit code is final
      return new ShellResult(p.ExitCode, stdoutTask.Result, stderrTask.Result);
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
      return new ShellResult(-1, "", ex.Message);
    }
#pragma warning restore CA1031 // Do not catch general exception types
  }

  /// <summary>Returns the last <paramref name="chars"/> characters of text (or the
  ///     whole string when it is shorter). Convenience for bounded output of shell results:
  ///     Output(Tail(r.Stdout, 2000)) never throws on short output, unlike r.Stdout[^N..].</summary>
  public static string Tail(string? text, int chars) => text is null || chars <= 0 ? "" : text.Length <= chars ? text : text[^chars..];
  /// <summary>Append a line to the script output. Does not terminate the script.
  /// Also capture any text written to Console.Out.</summary>
  public void Output(object? value)
  {
    string text = value switch
    {
      null => "",
      string s => s,
      _ => JsonSerializer.Serialize(value)
    };
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

/// <summary>Tool-calling surface exposed to C# scripts as Tools.read(...) etc.
/// Each registered action becomes a public method. The generic Invoke() is always
/// available for actions whose names aren't valid C# identifiers.</summary>
// Named decision (CA1707): convenience methods deliberately mirror the wire tool names
// (search_files, git_status, ...) — they are the model-facing script API, so underscores
// are the contract, not a style violation.
#pragma warning disable CA1707 // Identifiers should not contain underscores
public sealed class ScriptTools
{
  private readonly ICapabilityRegistry _registry;

  // Per-tool convenience methods — generated once at construction
  private readonly Dictionary<string, Func<object?, string>> _methods = [];

  public ScriptTools(ICapabilityRegistry registry, ScriptGlobals globals)
  {
    ArgumentNullException.ThrowIfNull(registry);
    ArgumentNullException.ThrowIfNull(globals);
    _registry = registry;

    foreach (string name in registry.Providers
        .SelectMany(p => p.Actions)
        .Select(a => a.Name)
        .Where(IsValidIdentifier))
    {
      _methods[name] = args => InvokeCore(name, args);
    }
  }

  /// <summary>Invoke a tool by name and return the raw tool result text.
  ///     Every invocation MUST carry timeoutSeconds (whole seconds, 1..3600): it is
  ///     validated here and STRIPPED from the arguments before dispatch so providers
  ///     never see a harness-reserved key. Enforcement follows the action's TimeoutPolicy:
  ///     HarnessEnforced actions are cancelled when the budget elapses (Error [ToolTimeout]);
  ///     SelfManaged actions apply their own declared budget internally (clarify waits on
  ///     the human without any bound), so the harness validates but never cancels on them.</summary>
  public string Invoke(string name, object? args)
  {
    Result<ResolvedCapability> resolved = _registry.Resolve(name);
    if (!resolved.IsSuccess)
    {
      return $"Error [UnknownAction]: {resolved.Error!.Message}";
    }

    string json = args switch
    {
      null => "{}",
      string s => s,
      _ => JsonSerializer.Serialize(args)
    };

    JsonElement document;
    try
    {
      using JsonDocument parsed = JsonDocument.Parse(json);
      document = parsed.RootElement.Clone();
    }
    catch (JsonException ex)
    {
      return $"Error [InvalidJsonArguments]: Arguments are not valid JSON: {ex.Message}";
    }
    if (document.ValueKind != JsonValueKind.Object)
    {
      return "Error [InvalidJsonArguments]: Arguments must be a JSON object.";
    }

    Result<TimeSpan> budget = ToolTimeout.Parse(document);
    if (!budget.IsSuccess)
    {
      return $"Error [{budget.Error!.Code}]: {budget.Error.Message}";
    }

    // Tools whose contract declares timeoutSeconds (ITool-backed actions) re-validate
    // it themselves — pass arguments through untouched. Pure capability providers never
    // declare it; strip the harness-reserved key so their strict parsers accept the rest.
    bool declaresTimeout = resolved.Value!.Action.Parameters
        .Any(pr => pr.Name == ToolTimeout.ParameterName);
    string stripped = declaresTimeout ? json : StripTimeout(document);

    // Offload to the worker pool before blocking: scripts are synchronous but the
    // registry is async, and awaiting inline deadlocks whenever this runs on a thread
    // whose SynchronizationContext must pump (e.g. Avalonia's UI thread). Task.Run
    // alone still FLOWS the ambient context (.NET 6+), so the flow is suppressed —
    // the invocation's continuation resumes on the pool, never on a blocked pump.
    Task<CapabilityInvocationResult> scheduled;
    using (ExecutionContext.SuppressFlow())
    {
      scheduled = Task.Run(async () =>
      {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        // SelfManaged actions own their budget; an infinite CancelAfter never fires,
        // and caller cancellation is not threaded here by design (see Invoke docs).
        cts.CancelAfter(resolved.Value.Action.Timeout == TimeoutPolicy.SelfManaged
                  ? Timeout.InfiniteTimeSpan
                  : budget.Value);
        try
        {
          return await _registry.InvokeAsync(resolved.Value, stripped, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
          return new CapabilityInvocationResult(ToolTimeout.TimedOut(name, budget.Value).Content, true);
        }
      });
    }

    CapabilityInvocationResult result = scheduled.GetAwaiter().GetResult();
    return result.Content;
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
  /// <summary>Dynamic dispatch for convenience methods generated as public methods.
  /// Matches unknown method calls by name if the tool name is valid C#. The argument
  /// defaults to null so zero-argument actions bind without a dummy object —
  /// Tools.git_status() and Tools.Invoke("git_status", null) are equivalent.</summary>
  public string read(object? args = null) => Invoke("read", args);
  public string write(object? args = null) => Invoke("write", args);
  public string edit(object? args = null) => Invoke("edit", args);
  public string search_files(object? args = null) => Invoke("search_files", args);
  public string exec(object? args = null) => Invoke("exec", args);
  public string git_status(object? args = null) => Invoke("git_status", args);
  public string working_diff(object? args = null) => Invoke("working_diff", args);
  public string git_commit(object? args = null) => Invoke("git_commit", args);

  public string List() => string.Join("\n", _registry.Providers.SelectMany(p => p.Actions)
      .Select(a => $"{a.Name}({string.Join(", ", a.Parameters.Select(p => $"{p.Name}: {p.Type}"))})"));

  public string Describe(string name)
  {
    Result<ResolvedCapability> resolved = _registry.Resolve(name);
    if (!resolved.IsSuccess)
    {
      return $"Error [UnknownAction]: {resolved.Error!.Message}";
    }

    ActionDescriptor action = resolved.Value!.Action;
    StringBuilder sb = new($"{action.Name} — {action.Summary}\n\n{action.Description}");
    foreach (ActionParameter p in action.Parameters)
    {
      _ = sb.Append(CultureInfo.InvariantCulture, $"\n- {p.Name}: {p.Type} — {p.Description}");
    }

    return sb.ToString();
  }

  private string InvokeCore(string name, object? args) => Invoke(name, args);

  private static bool IsValidIdentifier(string name)
      => !string.IsNullOrEmpty(name) && (char.IsLetter(name[0]) || name[0] == '_')
          && name.All(c => char.IsLetterOrDigit(c) || c == '_');
}

#pragma warning restore CA1707 // Identifiers should not contain underscores
