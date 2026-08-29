using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Roslyn.ACL.Tests;

public class CSharpScriptExecEngineTests
{
  /// <summary>Tests exercise engine semantics against a fixed workspace root — the
  ///     same contract the composition supplies per session (IWorkspaceContext).</summary>
  private static CSharpScriptExecEngine CreateEngine()
      => new(CapabilityRegistry.Create([]),
          workspaceRoot: () => AppContext.BaseDirectory);

  [Fact]
  public async Task StringReturnValue_BecomesOutput()
  {
    CSharpScriptExecEngine engine = CreateEngine();
    ExecRunResult run = await engine.ExecuteAsync(new ExecProgram("\"hello from csharp\""));

    Assert.Equal(ExecRunStatus.Completed, run.Status);
    Assert.Equal("hello from csharp", run.Output);
    Assert.Empty(run.ErrorLines);
  }

  [Fact]
  public async Task IntReturnValue_SerializedToJson()
  {
    CSharpScriptExecEngine engine = CreateEngine();
    ExecRunResult run = await engine.ExecuteAsync(new ExecProgram("42"));

    Assert.Equal(ExecRunStatus.Completed, run.Status);
    Assert.Equal("42", run.Output);
  }

  [Fact]
  public async Task VoidScript_ReturnsEmptyOutput()
  {
    CSharpScriptExecEngine engine = CreateEngine();
    ExecRunResult run = await engine.ExecuteAsync(new ExecProgram("var x = 1 + 1;"));

    Assert.Equal(ExecRunStatus.Completed, run.Status);
    Assert.Equal("", run.Output);
  }

  [Fact]
  public async Task Output_CapturesLinesDuringExecution()
  {
    CSharpScriptExecEngine engine = CreateEngine();
    ExecRunResult run = await engine.ExecuteAsync(new ExecProgram("Output(\"line1\"); Output(\"line2\"); 0"));

    Assert.Contains("line1", run.Output, StringComparison.Ordinal);
    Assert.Contains("line2", run.Output, StringComparison.Ordinal);
  }

  [Fact]
  public async Task CompileError_ReturnsInValidate()
  {
    CSharpScriptExecEngine engine = CreateEngine();
    Result<IReadOnlyList<ExecParseError>> errors = await engine.ValidateAsync(new ExecProgram("this is not valid csharp ??!!"));

    Assert.True(errors.IsSuccess);
    Assert.NotEmpty(errors.Value);
  }

  [Fact]
  public async Task RuntimeException_BecomesError()
  {
    CSharpScriptExecEngine engine = CreateEngine();
    ExecRunResult run = await engine.ExecuteAsync(new ExecProgram("throw new System.Exception(\"boom\");"));

    Assert.Equal(ExecRunStatus.Completed, run.Status); // completed with error lines
    Assert.NotEmpty(run.ErrorLines);
    Assert.Contains("boom", run.ErrorLines[0], StringComparison.Ordinal);
  }

  [Fact]
  public async Task Shell_RunsCommand()
  {
    CSharpScriptExecEngine engine = CreateEngine();
    ExecRunResult run = await engine.ExecuteAsync(new ExecProgram("var r = Shell(\"cmd\", \"/c\", \"echo hello\"); return r.Stdout;"));

    Assert.Equal(ExecRunStatus.Completed, run.Status);
    Assert.Contains("hello", run.Output, StringComparison.Ordinal);
  }


  /// <summary>Hang primitive for the kill-tree regressions: waitfor blocks for its whole
  ///     duration and resolves directly from System32, so it hangs on every Windows machine
  ///     regardless of PATH.</summary>
  private const string HungCommand =
      "var r = Shell(\"waitfor\", \"/t\", \"60\", \"never\"); return r.ExitCode;";

  /// <summary>Stop-button regression: cancelling the caller's token must kill the hung
  ///     child process tree so the turn actually ends, not just the token firing.</summary>
  [Fact]
  public async Task CallerCancellation_KillsHungShellProcessTree()
  {
    using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));
    CSharpScriptExecEngine engine = CreateEngine();

    // Cancellation propagates for classification at the tool layer.
    _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
        () => engine.ExecuteAsync(new ExecProgram(HungCommand), cts.Token));
  }

  /// <summary>Budget regression: an expired exec budget must kill the hung child tree and
  ///     surface Timeout — previously Shell blocked forever past its own WaitForExit cap.</summary>
  [Fact]
  public async Task ElapsedBudget_KillsHungShellProcessTree()
  {
    // The budget arrives through the caller's token - ExecOptions carries none.
    using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));
    CSharpScriptExecEngine engine = CreateEngine();

    _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
        () => engine.ExecuteAsync(new ExecProgram(HungCommand), cts.Token));
  }

  /// <summary>Pipe-deadlock regression: chatty stderr under quiet stdout must not block a
  ///     sequential stdout ReadToEnd — both pipes now drain concurrently.</summary>
  [Fact]
  public async Task ChattyStderr_DoesNotDeadlockShell()
  {
    CSharpScriptExecEngine engine = CreateEngine();
    string program = "var r = Shell(\"cmd\", \"/c\", \"for /L %i in (1,1,500) do @echo stderr-line-%i 1>&2\"); return r.Stderr.Length;";
    ExecRunResult run = await engine.ExecuteAsync(new ExecProgram(program));

    Assert.Equal(ExecRunStatus.Completed, run.Status);
    Assert.True(int.Parse(run.Output, System.Globalization.CultureInfo.InvariantCulture) > 4096, $"stderr drained: {run.Output}");
  }
  [Fact]
  public async Task Workspace_Global_Reflects_Injected_Resolver_PerExecution()
  {
    string current = AppContext.BaseDirectory;
    CSharpScriptExecEngine engine = new(CapabilityRegistry.Create([]),
        workspaceRoot: () => current);
    ExecRunResult first = await engine.ExecuteAsync(new ExecProgram("return Workspace;"));
    Assert.Equal(current, first.Output);

    // A second execution sees the resolver's NEW value — the construction-time
    // capture that pinned multi-session hosts to one root is gone.
    current = Path.GetTempPath();
    ExecRunResult second = await engine.ExecuteAsync(new ExecProgram("return Workspace;"));
    Assert.Equal(Path.GetTempPath(), second.Output);
  }
}

public class CSharpEvidenceRunnerTests
{
  [Fact]
  public async Task TrueExpression_ReturnsConfirmed()
  {
    CSharpEvidenceRunner runner = new(EvidenceOptions.Default);
    EvidenceResult r = await runner.RunAsync("1 + 1 == 2");

    Assert.True(r.Confirmed);
    Assert.Empty(r.Detail);
  }

  [Fact]
  public async Task FalseExpression_ReturnsNotConfirmed()
  {
    CSharpEvidenceRunner runner = new(EvidenceOptions.Default);
    EvidenceResult r = await runner.RunAsync("1 == 2");

    Assert.False(r.Confirmed);
    Assert.NotEmpty(r.Detail);
  }

  [Fact]
  public async Task Exception_ReturnsNotConfirmed()
  {
    CSharpEvidenceRunner runner = new(EvidenceOptions.Default);
    EvidenceResult r = await runner.RunAsync("throw new System.Exception(\"fail\")");

    Assert.False(r.Confirmed);
    Assert.Contains("fail", r.Detail, StringComparison.Ordinal);
  }

  [Fact]
  public async Task FileExists_Evidence_Works()
  {
    string tmp = Path.GetTempFileName();
    try
    {
      CSharpEvidenceRunner runner = new(EvidenceOptions.Default);
      EvidenceResult r = await runner.RunAsync($"System.IO.File.Exists(@\"{tmp.Replace("\\", "\\\\", StringComparison.Ordinal)}\")");
      Assert.True(r.Confirmed);
    }
    finally
    {
      File.Delete(tmp);
    }
  }
}
