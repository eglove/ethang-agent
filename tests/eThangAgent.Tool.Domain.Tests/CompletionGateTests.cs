using eThangAgent.ToolDomain.Verification;

namespace eThangAgent.ToolDomain.Tests;

// The completion gate's rule is freshness: a green run from an hour ago followed by five
// edits proves nothing about the code being declared done. The ledger is the only input -
// the harness ships no command set, so whatever the session ran IS its verification.
public class CompletionGateTests
{
  private const string Root = "C:\\ws";

  private static readonly DateTimeOffset Base = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

  private static CompletionGate Gate(WorkspaceSnapshot current, params ShellExecutionRecord[] runs) =>
      new(new FakeLedger(runs), Freshness(), new FixedSnapshotAccess(current), () => Root);

  private static VerificationFreshnessSpecification Freshness() =>
      new(new VerificationCommandSpecification(["dotnet test"]));

  private static WorkspaceSnapshot Tree(DateTimeOffset lastWriteUtc, params (string Path, ulong Hash)[] files) =>
      WorkspaceSnapshot.Create(Root, files.ToDictionary(
          f => f.Path,
          f => new WorkspaceFileFingerprint(10, lastWriteUtc.UtcDateTime, f.Hash),
          StringComparer.OrdinalIgnoreCase));

  private static ShellExecutionRecord Run(int exitCode, DateTimeOffset started) =>
      new(["dotnet", "test"], exitCode, started, started.AddMinutes(1));

  [Fact]
  public async Task NoRunAtAll_BlocksAndNamesTheRemedy()
  {
    CompletionVerdict verdict = await Gate(Tree(Base))
        .EvaluateAsync(TestContext.Current.CancellationToken);

    Assert.Equal(VerificationGateStatus.NeverRan, verdict.Status);
    Assert.True(verdict.BlocksCompletion);
    Assert.Contains("exit code 0", verdict.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task FailedRun_Blocks()
  {
    CompletionVerdict verdict = await Gate(Tree(Base), Run(1, Base))
        .EvaluateAsync(TestContext.Current.CancellationToken);

    Assert.Equal(VerificationGateStatus.Failed, verdict.Status);
    Assert.True(verdict.BlocksCompletion);
    Assert.Contains("exited 1", verdict.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task PassingRunAfterTheLastChange_IsFresh()
  {
    CompletionVerdict verdict = await Gate(Tree(Base.AddMinutes(-5)), Run(0, Base))
        .EvaluateAsync(TestContext.Current.CancellationToken);

    Assert.Equal(VerificationGateStatus.Fresh, verdict.Status);
    Assert.False(verdict.BlocksCompletion);
  }

  // THE POINT OF THE FEATURE: a green run followed by an edit is not evidence.
  [Fact]
  public async Task PassingRunThenAnEdit_IsStaleAndBlocks()
  {
    CompletionVerdict verdict = await Gate(Tree(Base.AddMinutes(5), ("a.cs", 2)), Run(0, Base))
        .EvaluateAsync(TestContext.Current.CancellationToken);

    Assert.Equal(VerificationGateStatus.Stale, verdict.Status);
    Assert.True(verdict.BlocksCompletion);
    Assert.Contains("CHANGED since", verdict.Message, StringComparison.Ordinal);
  }

  // Every refusal names the honest exit, or the cheapest way past it is a lie.
  [Fact]
  public async Task EveryRefusal_NamesAbandonedAsTheExit()
  {
    CompletionVerdict stale = await Gate(Tree(Base.AddMinutes(5), ("a.cs", 2)), Run(0, Base))
        .EvaluateAsync(TestContext.Current.CancellationToken);

    Assert.Contains("Abandoned", stale.Message, StringComparison.Ordinal);
  }

  // A passing run that STARTED before the edit but FINISHED after it still cannot cover it:
  // an edit landing mid-run carries a later write time, so the run predates the code.
  [Fact]
  public async Task EditDuringTheRun_IsStale()
  {
    ShellExecutionRecord run = new(["dotnet", "test"], 0, Base, Base.AddMinutes(10));
    CompletionVerdict verdict = await Gate(Tree(Base.AddMinutes(5), ("a.cs", 2)), run)
        .EvaluateAsync(TestContext.Current.CancellationToken);

    Assert.Equal(VerificationGateStatus.Stale, verdict.Status);
  }

  // A command the harness does not recognise as verification-class proves nothing.
  [Fact]
  public async Task UnrelatedCommand_IsNotAVerificationRun()
  {
    ShellExecutionRecord git = new(["git", "status"], 0, Base, Base.AddMinutes(1));
    CompletionVerdict verdict = await Gate(Tree(Base.AddMinutes(-5)), git)
        .EvaluateAsync(TestContext.Current.CancellationToken);

    Assert.Equal(VerificationGateStatus.NeverRan, verdict.Status);
  }

  // Unverifiable never blocks: refusing here would trap the claimant with no exit, since
  // re-running verification cannot fix an unreadable tree.
  [Fact]
  public async Task TruncatedWalk_IsUnverifiableAndDoesNotBlock()
  {
    WorkspaceSnapshot truncated = WorkspaceSnapshot.Create(Root,
        new Dictionary<string, WorkspaceFileFingerprint>(StringComparer.OrdinalIgnoreCase)
        {
          ["a.cs"] = new(10, Base.AddMinutes(5).UtcDateTime, 1),
        }, isComplete: false);

    CompletionVerdict verdict = await Gate(truncated, Run(0, Base))
        .EvaluateAsync(TestContext.Current.CancellationToken);

    Assert.Equal(VerificationGateStatus.Unverifiable, verdict.Status);
    Assert.False(verdict.BlocksCompletion);
  }

  [Fact]
  public async Task DisabledGate_NeverBlocks()
  {
    CompletionGate gate = new(new FakeLedger(), Freshness(),
        new FixedSnapshotAccess(Tree(Base)), () => Root, enabled: false);

    CompletionVerdict verdict = await gate.EvaluateAsync(TestContext.Current.CancellationToken);

    Assert.Equal(VerificationGateStatus.Fresh, verdict.Status);
    Assert.False(verdict.BlocksCompletion);
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
