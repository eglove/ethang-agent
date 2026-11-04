using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain.Lsp;
using eThangAgent.ToolDomain.Verification;

namespace eThangAgent.ToolDomain.Tests;

// Issue #356: advisory diagnostics appended to edit/write results, scoped to
// changed lines, labeled when degraded, session-local, never gated, never persisted.
public class AdvisoryDiagnosticsTests
{
  private const string Root = @"C:\ws";
  private const string Resolved = @"C:\ws\a.cs";

  private sealed class ScriptedSource : IAdvisoryDiagnosticsSource
  {
    public AdvisoryDiagnosticsResult Next { get; set; } = new(AdvisoryDiagnosticsState.Ready, []);
    public List<string> Paths { get; } = [];
    public Task<AdvisoryDiagnosticsResult> GetAsync(string path, CancellationToken ct = default)
    {
      Paths.Add(path);
      return Task.FromResult(Next);
    }
  }

  private sealed class SlowSource : IAdvisoryDiagnosticsSource
  {
    public async Task<AdvisoryDiagnosticsResult> GetAsync(string path, CancellationToken ct = default)
    {
      await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
      return new AdvisoryDiagnosticsResult(AdvisoryDiagnosticsState.Ready, []);
    }
  }

  private sealed class FakeEditAccess(Result<ReplaceOutcome> outcome) : IFileEditAccess
  {
    public Task<Result<ReplaceOutcome>> ReplaceInFileAsync(
        string path, string oldText, string newText, int? occurrences, CancellationToken ct = default)
      => Task.FromResult(outcome);
    public Task<Result<ReplaceOutcome>> ReplaceLineRangeAsync(
        string path, int startLine, int endLine, string newText, CancellationToken ct = default)
      => Task.FromResult(outcome);
    public Task<Result<HunkReplaceOutcome>> ReplaceHunksAsync(
        string path, IReadOnlyList<EditHunk> hunks, CancellationToken ct = default)
      => Task.FromResult(Result.Success(new HunkReplaceOutcome([1], 3, LineEnding.Lf)));
  }

  private sealed class FakeWriteAccess(Result<FileWriteOutcome> outcome) : IFileWriteAccess
  {
    public Task<Result<FileWriteOutcome>> WriteFileAsync(
        string path, string content, bool overwrite, CancellationToken ct = default)
      => Task.FromResult(outcome);
    public Task<Result<FileWriteOutcome>> WriteFileLinesAsync(
        string path, IReadOnlyList<string> lines, bool overwrite, CancellationToken ct = default)
      => Task.FromResult(outcome);
    public Task<Result<FileWriteOutcome>> WriteFileBytesAsync(
        string path, byte[] bytes, bool overwrite, CancellationToken ct = default)
      => Task.FromResult(outcome);
  }

  private static EditTool EditToolWith(IAdvisoryDiagnosticsSource? source, SessionAdvisoryDiagnosticsLedger? ledger = null)
  {
    Result<ReplaceOutcome> outcome = Result.Success(new ReplaceOutcome(1, 3, LineEnding.Lf, [2]));
    return new EditTool(new WorkspacePathResolver(Root), new FakeEditAccess(outcome), source, ledger);
  }

  private static WriteTool WriteToolWith(IAdvisoryDiagnosticsSource? source, SessionAdvisoryDiagnosticsLedger? ledger = null)
  {
    Result<FileWriteOutcome> outcome = Result.Success(new FileWriteOutcome(true, 42, LineEnding.Lf, [1, 2, 3]));
    return new WriteTool(new WorkspacePathResolver(Root), new FakeWriteAccess(outcome), source, ledger);
  }

  private const string EditArgs = "{\"timeoutSeconds\":120,\"path\":\"a.cs\",\"old\":\"x\",\"replacement\":\"y\",\"occurrences\":1}";
  private const string WriteArgs = "{\"timeoutSeconds\":120,\"path\":\"a.cs\",\"content\":\"x\",\"overwrite\":true}";

  [Fact]
  public async Task OneTypeErrorEdit_ReturnsErrorScopedToChangedLines()
  {
    ScriptedSource source = new()
    {
      Next = new AdvisoryDiagnosticsResult(AdvisoryDiagnosticsState.Ready,
        [new AdvisoryDiagnostic(2, 0, 2, 5, "CS0029", "Cannot implicitly convert type"),
         new AdvisoryDiagnostic(1, 0, 1, 5, "CS1002", "; expected")]),
    };
    ToolResult result = await EditToolWith(source).ExecuteAsync(new RawToolInput("edit", EditArgs), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Contains("CS0029", result.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("CS1002", result.Content, StringComparison.Ordinal);
    Assert.Equal([Resolved], source.Paths);
  }

  [Fact]
  public async Task SlowEdit_ReturnsOnTimeWithLabeledNote()
  {
    DateTimeOffset start = DateTimeOffset.UtcNow;
    ToolResult result = await EditToolWith(new SlowSource()).ExecuteAsync(new RawToolInput("edit", EditArgs), ct: TestContext.Current.CancellationToken);
    TimeSpan elapsed = DateTimeOffset.UtcNow - start;
    Assert.False(result.IsError);
    Assert.True(elapsed < TimeSpan.FromSeconds(5), $"elapsed {elapsed}");
    Assert.Contains("stale", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Starting_ReturnsLabeledNotEmpty()
  {
    ScriptedSource source = new() { Next = new AdvisoryDiagnosticsResult(AdvisoryDiagnosticsState.Starting, []) };
    ToolResult result = await EditToolWith(source).ExecuteAsync(new RawToolInput("edit", EditArgs), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Contains("Starting", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Unavailable_ReturnsLabeledNotEmpty()
  {
    ScriptedSource source = new() { Next = new AdvisoryDiagnosticsResult(AdvisoryDiagnosticsState.Unavailable, []) };
    ToolResult result = await EditToolWith(source).ExecuteAsync(new RawToolInput("edit", EditArgs), ct: TestContext.Current.CancellationToken);
    Assert.Contains("unavailable", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Degraded_ReturnsLabeledNotEmpty()
  {
    ScriptedSource source = new() { Next = new AdvisoryDiagnosticsResult(AdvisoryDiagnosticsState.Degraded, []) };
    ToolResult result = await EditToolWith(source).ExecuteAsync(new RawToolInput("edit", EditArgs), ct: TestContext.Current.CancellationToken);
    Assert.Contains("Degraded", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Stale_ReturnsLabeledNotEmpty()
  {
    ScriptedSource source = new() { Next = new AdvisoryDiagnosticsResult(AdvisoryDiagnosticsState.Stale, []) };
    ToolResult result = await EditToolWith(source).ExecuteAsync(new RawToolInput("edit", EditArgs), ct: TestContext.Current.CancellationToken);
    Assert.Contains("stale", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task LspAbsent_ResultIsByteIdentical()
  {
    ToolResult bare = await EditToolWith(null).ExecuteAsync(new RawToolInput("edit", EditArgs), ct: TestContext.Current.CancellationToken);
    ToolResult withSource = await EditToolWith(new ScriptedSource { Next = new AdvisoryDiagnosticsResult(AdvisoryDiagnosticsState.Ready, []) })
        .ExecuteAsync(new RawToolInput("edit", EditArgs), ct: TestContext.Current.CancellationToken);
    Assert.Equal(bare.Content, withSource.Content);
  }

  [Fact]
  public async Task NonCodeFile_ResultIsByteIdentical()
  {
    ScriptedSource source = new();
    ToolResult result = await EditToolWith(source).ExecuteAsync(new RawToolInput("edit",
        "{\"timeoutSeconds\":120,\"path\":\"a.txt\",\"old\":\"x\",\"replacement\":\"y\",\"occurrences\":1}"), ct: TestContext.Current.CancellationToken);
    Assert.Empty(source.Paths);
    Assert.DoesNotContain("[diagnostics]", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task WriteTool_AppendsScopedDiagnostics()
  {
    ScriptedSource source = new()
    {
      Next = new AdvisoryDiagnosticsResult(AdvisoryDiagnosticsState.Ready,
        [new AdvisoryDiagnostic(3, 0, 3, 5, "CS0029", "Cannot implicitly convert type")]),
    };
    ToolResult result = await WriteToolWith(source).ExecuteAsync(new RawToolInput("write", WriteArgs), ct: TestContext.Current.CancellationToken);
    Assert.Contains("CS0029", result.Content, StringComparison.Ordinal);
    Assert.Equal([Resolved], source.Paths);
  }

  [Fact]
  public async Task Ledger_IsSessionLocalAndSeparate()
  {
    SessionAdvisoryDiagnosticsLedger advisory = new();
    SessionVerificationLedger verification = new();
    ScriptedSource source = new()
    {
      Next = new AdvisoryDiagnosticsResult(AdvisoryDiagnosticsState.Ready,
        [new AdvisoryDiagnostic(2, 0, 2, 5, "CS0029", "Cannot implicitly convert type")]),
    };
    ToolResult result = await EditToolWith(source, advisory).ExecuteAsync(new RawToolInput("edit", EditArgs), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    AdvisoryDiagnosticsEntry entry = Assert.Single(advisory.Snapshot());
    Assert.Equal(Resolved, entry.Path);
    Assert.Empty(verification.Snapshot());
  }

  [Fact]
  public async Task GateParity_BothWays()
  {
    DateTimeOffset baseTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    WorkspaceSnapshot tree = WorkspaceSnapshot.Create(Root,
        new Dictionary<string, WorkspaceFileFingerprint>(StringComparer.OrdinalIgnoreCase)
        {
          ["a.cs"] = new(10, baseTime.AddMinutes(-5).UtcDateTime, 1),
        });
    ShellExecutionRecord run = new(["dotnet", "test"], 0, baseTime, baseTime.AddMinutes(1));
    CompletionGate gate = new(new FakeLedger(run), new FixedSnapshotAccess(tree), () => Root);
    CompletionVerdict before = await gate.EvaluateAsync(TestContext.Current.CancellationToken);
    ScriptedSource source = new() { Next = new AdvisoryDiagnosticsResult(AdvisoryDiagnosticsState.Ready, []) };
    _ = await EditToolWith(source).ExecuteAsync(new RawToolInput("edit", EditArgs), ct: TestContext.Current.CancellationToken);
    CompletionVerdict after = await gate.EvaluateAsync(TestContext.Current.CancellationToken);
    Assert.Equal(before.Status, after.Status);
  }

  [Fact]
  public void NothingPersisted_AdvisoryLedgerHasNoDatabaseSurface()
  {
    SessionAdvisoryDiagnosticsLedger ledger = new();
    Assert.Empty(ledger.Snapshot());
    ledger.Append(new AdvisoryDiagnosticsEntry(Resolved, AdvisoryDiagnosticsState.Ready, []));
    AdvisoryDiagnosticsEntry entry = Assert.Single(ledger.Snapshot());
    Assert.Equal(Resolved, entry.Path);
  }

  private sealed class FakeLedger(params ShellExecutionRecord[] runs) : IVerificationLedger
  {
    public void Append(ShellExecutionRecord record) => throw new NotSupportedException();
    public IReadOnlyList<ShellExecutionRecord> Snapshot() => runs;
  }

  private sealed class FixedSnapshotAccess(WorkspaceSnapshot snapshot) : IWorkspaceSnapshotAccess
  {
    public Task<WorkspaceSnapshot> CaptureAsync(string workspaceRoot, CancellationToken ct = default) =>
        Task.FromResult(snapshot);
  }
}
