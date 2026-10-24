using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Roslyn.ACL.Tests;

/// <summary>Issue #259 end to end: a nested call whose ARGUMENTS are refused before the
///     tool does any work returns an in-band <c>Error [Code]:</c> string through the real
///     edit/write validators, so the surrounding program keeps running and can branch on
///     it. Structural failures (unknown action, malformed arguments, a value the wrong
///     type) and errors reported from work already performed still throw and end the
///     program.</summary>
public class ExecContractErrorScriptTests
{
  private const string Root = @"C:\ws";

  /// <summary>Script lines are joined with this instead of an escape sequence, so no
  ///     source line in this file carries a backslash escape.</summary>
  private static readonly string NL = ((char)10).ToString();

  private static readonly string[] NoSelectorScript =
  [
    @"var r = Tools.edit(new { path = ""a.txt"", old = ""x"", replacement = ""y"" });",
    @"Output(""continued after the refusal"");",
    @"return r;",
  ];

  private static readonly string[] BranchOnRefusalScript =
  [
    @"var r = Tools.edit(new { path = ""a.txt"", old = ""x"", replacement = ""y"" });",
    @"if (r.StartsWith(""Error [InvalidParameterValue]"", StringComparison.Ordinal))",
    @"{",
    @"    return ""would retry with occurrences = 1"";",
    @"}",
    @"return ""unexpected: "" + r;",
  ];

  private static CSharpScriptExecEngine Engine() =>
      EngineWith(new ThrowingEditAccess(), new ThrowingWriteAccess());

  private static CSharpScriptExecEngine EngineWith(IFileEditAccess edit, IFileWriteAccess write)
  {
    AgentToolsProvider tools = new("tools",
    [
        new AgentToolBinding(new EditTool(new WorkspacePathResolver(Root), edit), "Edits a file."),
        new AgentToolBinding(new WriteTool(new WorkspacePathResolver(Root), write), "Writes a file."),
    ]);
    return new CSharpScriptExecEngine(CapabilityRegistry.Create([tools]),
        workspaceRoot: () => Root);
  }

  private static Task<ExecRunResult> RunAsync(string program) =>
      Engine().ExecuteAsync(new ExecProgram(program, TimeSpan.FromSeconds(30)),
          ct: TestContext.Current.CancellationToken);

  // ---- The selector omission that used to kill the program (criterion 1) ----

  [Fact]
  public async Task EditWithNoSelector_StaysInBand_AndTheScriptContinues()
  {
    ExecRunResult run = await RunAsync(string.Join(NL, NoSelectorScript));

    Assert.Equal(ExecRunStatus.Completed, run.Status);
    Assert.Empty(run.ErrorLines);
    Assert.Contains("continued after the refusal", run.Output, StringComparison.Ordinal);
    Assert.Contains("Error [InvalidParameterValue]: nested call 'edit':", run.Output, StringComparison.Ordinal);
    Assert.Contains("exactly one of 'all'", run.Output, StringComparison.Ordinal);
  }

  // The refusal is branchable exactly like an environmental error: read the code, add the
  // argument it names, call again — inside the SAME program, no round trip.
  [Fact]
  public async Task EditSelectorRefusal_IsBranchable_AndTheCorrectedCallProceeds()
  {
    ExecRunResult run = await RunAsync(string.Join(NL, BranchOnRefusalScript));

    Assert.Equal(ExecRunStatus.Completed, run.Status);
    Assert.Empty(run.ErrorLines);
    Assert.Equal("would retry with occurrences = 1", run.Output);
  }

  // ---- write's content xor lines is the same class of omission (criterion 3) ----

  [Fact]
  public async Task WriteWithNeitherContentNorLines_StaysInBand()
  {
    ExecRunResult run = await RunAsync(@"var r = Tools.write(new { path = ""a.txt"" });"
        + NL + @"return r;");

    Assert.Equal(ExecRunStatus.Completed, run.Status);
    Assert.Empty(run.ErrorLines);
    Assert.Contains("Error [InvalidParameterValue]: nested call 'write':", run.Output, StringComparison.Ordinal);
    Assert.Contains("exactly one of 'content'", run.Output, StringComparison.Ordinal);
  }

  // ---- Structural failures keep throwing (criterion 2) ----

  [Fact]
  public async Task UnknownAction_StillThrows()
  {
    ExecRunResult run = await RunAsync(@"Tools.Invoke(""nope"", new { timeoutSeconds = 30 });");

    Assert.NotEmpty(run.ErrorLines);
    Assert.Contains("Error [UnknownAction]", run.ErrorLines[0], StringComparison.Ordinal);
  }

  [Fact]
  public async Task MalformedJsonArguments_StillThrow()
  {
    ExecRunResult run = await RunAsync(@"Tools.Invoke(""edit"", ""{ not json"");");

    Assert.NotEmpty(run.ErrorLines);
    Assert.Contains("Error [InvalidJsonArguments]", run.ErrorLines[0], StringComparison.Ordinal);
  }

  [Fact]
  public async Task NonObjectArguments_StillThrow()
  {
    ExecRunResult run = await RunAsync(@"Tools.Invoke(""edit"", ""[1,2]"");");

    Assert.NotEmpty(run.ErrorLines);
    Assert.Contains("Error [InvalidJsonArguments]", run.ErrorLines[0], StringComparison.Ordinal);
  }

  // A WRONG TYPE was never in the contract-code set, so it keeps the behaviour it always
  // had — in-band. This test pins the boundary from the other side: widening the
  // recoverable set to "every argument-shape code" would have silently changed it.
  [Fact]
  public async Task WrongParameterType_StaysInBand_AsBefore()
  {
    ExecRunResult run = await RunAsync(
        @"return Tools.edit(new { path = ""a.txt"", old = 5, replacement = ""y"", occurrences = 1 });");

    Assert.Equal(ExecRunStatus.Completed, run.Status);
    Assert.Empty(run.ErrorLines);
    Assert.Contains("Error [InvalidParameterType]", run.Output, StringComparison.Ordinal);
  }

  // A failure the tool reports AFTER it did work carries its own code, and stays in-band
  // exactly as it always has: the model reads OccurrenceMismatch and corrects the count.
  // The harness's throw for a POST-work call returning a CONTRACT code is pinned in
  // ScriptToolsContractTests, because no shipped backend raises one today.
  [Fact]
  public async Task BackendErrorAfterWork_StaysInBand()
  {
    CSharpScriptExecEngine engine = EngineWith(new MismatchEditAccess(), new ThrowingWriteAccess());
    ExecRunResult run = await engine.ExecuteAsync(
        new ExecProgram(
            @"return Tools.edit(new { path = ""a.txt"", old = ""x"", replacement = ""y"", occurrences = 1 });",
            TimeSpan.FromSeconds(30)),
        ct: TestContext.Current.CancellationToken);

    Assert.Equal(ExecRunStatus.Completed, run.Status);
    Assert.Empty(run.ErrorLines);
    Assert.Contains("Error [OccurrenceMismatch]", run.Output, StringComparison.Ordinal);
  }

  // Fakes: no test here touches the disk. Only the argument validators matter — a refused
  // call must never reach the backend, so the access throws if it is touched.
  private sealed class ThrowingEditAccess : IFileEditAccess
  {
    public Task<Result<ReplaceOutcome>> ReplaceInFileAsync(
        string path, string oldText, string newText, int? occurrences, CancellationToken ct = default) =>
        throw new InvalidOperationException("the validator must refuse before dispatch");

    public Task<Result<ReplaceOutcome>> ReplaceLineRangeAsync(
        string path, int startLine, int endLine, string newText, CancellationToken ct = default) =>
        throw new InvalidOperationException("the validator must refuse before dispatch");

    public Task<Result<HunkReplaceOutcome>> ReplaceHunksAsync(
        string path, IReadOnlyList<EditHunk> hunks, CancellationToken ct = default) =>
        throw new InvalidOperationException("the validator must refuse before dispatch");
  }

  private sealed class ThrowingWriteAccess : IFileWriteAccess
  {
    public Task<Result<FileWriteOutcome>> WriteFileAsync(
        string path, string content, bool overwrite, CancellationToken ct = default) =>
        throw new InvalidOperationException("the validator must refuse before dispatch");

    public Task<Result<FileWriteOutcome>> WriteFileLinesAsync(
        string path, IReadOnlyList<string> lines, bool overwrite, CancellationToken ct = default) =>
        throw new InvalidOperationException("the validator must refuse before dispatch");

    public Task<Result<FileWriteOutcome>> WriteFileBytesAsync(
        string path, byte[] bytes, bool overwrite, CancellationToken ct = default) =>
        throw new InvalidOperationException("the validator must refuse before dispatch");
  }

  private sealed class MismatchEditAccess : IFileEditAccess
  {
    public Task<Result<ReplaceOutcome>> ReplaceInFileAsync(
        string path, string oldText, string newText, int? occurrences, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<ReplaceOutcome>(new DomainError("OccurrenceMismatch",
            "The anchor text occurs 2 time(s), but 'occurrences' was 1.")));

    public Task<Result<ReplaceOutcome>> ReplaceLineRangeAsync(
        string path, int startLine, int endLine, string newText, CancellationToken ct = default) =>
        throw new InvalidOperationException("not used");

    public Task<Result<HunkReplaceOutcome>> ReplaceHunksAsync(
        string path, IReadOnlyList<EditHunk> hunks, CancellationToken ct = default) =>
        throw new InvalidOperationException("not used");
  }
}
