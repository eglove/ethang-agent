namespace eThangAgent.ToolDomain.Tests;

// Issue #283 Part 2 acceptance. The freshness rule is the whole idea: a green run from
// an hour ago followed by five edits proves nothing about the code being declared done.
public class CompletionGateTests
{
  private const string Root = "C:\\ws";

  private static CompletionGate Gate(WorkspaceSnapshot current) =>
      new(new FixedSnapshotAccess(current));

  private static WorkspaceSnapshot Snapshot(params (string Path, ulong Hash)[] files) =>
      WorkspaceSnapshot.Create(Root, files.ToDictionary(
          f => f.Path,
          f => new WorkspaceFileFingerprint(10, DateTime.UtcNow, f.Hash),
          StringComparer.OrdinalIgnoreCase));

  private static string Record(string? digest, bool passed) =>
      new GoalGateRecord(digest, passed, DateTimeOffset.UtcNow).ToJson();

  // A workspace with no gate must NEVER block: the check is simply unavailable.
  [Fact]
  public async Task UnsetGate_NeverBlocks()
  {
    CompletionGateVerdict verdict = await Gate(Snapshot(("a.cs", 1)))
        .EvaluateAsync(Root, gateConfigured: false, lastCheckJson: null,
            TestContext.Current.CancellationToken);

    Assert.Equal(GoalGateStatus.Unset, verdict.Status);
    Assert.False(verdict.BlocksCompletion);
  }

  [Fact]
  public async Task ConfiguredNeverRun_Blocks()
  {
    CompletionGateVerdict verdict = await Gate(Snapshot(("a.cs", 1)))
        .EvaluateAsync(Root, gateConfigured: true, lastCheckJson: null,
            TestContext.Current.CancellationToken);

    Assert.Equal(GoalGateStatus.NeverRan, verdict.Status);
    Assert.True(verdict.BlocksCompletion);
    Assert.Contains("state.checkgoal", verdict.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task FailedRun_Blocks()
  {
    WorkspaceSnapshot ws = Snapshot(("a.cs", 1));
    CompletionGateVerdict verdict = await Gate(ws)
        .EvaluateAsync(Root, true, Record(ws.Digest, passed: false),
            TestContext.Current.CancellationToken);

    Assert.Equal(GoalGateStatus.Failed, verdict.Status);
    Assert.True(verdict.BlocksCompletion);
  }

  [Fact]
  public async Task PassedAndUnchanged_IsFresh()
  {
    WorkspaceSnapshot ws = Snapshot(("a.cs", 1));
    CompletionGateVerdict verdict = await Gate(ws)
        .EvaluateAsync(Root, true, Record(ws.Digest, passed: true),
            TestContext.Current.CancellationToken);

    Assert.Equal(GoalGateStatus.Fresh, verdict.Status);
    Assert.False(verdict.BlocksCompletion);
  }

  // THE POINT OF THE FEATURE: a green run followed by an edit is not evidence.
  [Fact]
  public async Task PassedThenWorkspaceChanged_IsStaleAndBlocks()
  {
    WorkspaceSnapshot atRunTime = Snapshot(("a.cs", 1));
    WorkspaceSnapshot now = Snapshot(("a.cs", 2));
    CompletionGateVerdict verdict = await Gate(now)
        .EvaluateAsync(Root, true, Record(atRunTime.Digest, passed: true),
            TestContext.Current.CancellationToken);

    Assert.Equal(GoalGateStatus.Stale, verdict.Status);
    Assert.True(verdict.BlocksCompletion);
    Assert.Contains("CHANGED since", verdict.Message, StringComparison.Ordinal);
    Assert.Contains("state.checkgoal", verdict.Message, StringComparison.Ordinal);
  }

  // Every refusal names the honest exit, or the cheapest way past it is a lie.
  [Fact]
  public async Task EveryRefusal_NamesAbandonedAsTheExit()
  {
    WorkspaceSnapshot atRunTime = Snapshot(("a.cs", 1));
    CompletionGateVerdict stale = await Gate(Snapshot(("a.cs", 2)))
        .EvaluateAsync(Root, true, Record(atRunTime.Digest, passed: true),
            TestContext.Current.CancellationToken);

    Assert.Contains("Abandoned", stale.Message, StringComparison.Ordinal);
  }

  // A rewrite that changed nothing must not invalidate a green gate - the digest is
  // content-true, so it is not fooled by a moved timestamp.
  [Fact]
  public async Task NoOpRewrite_KeepsTheGateFresh()
  {
    WorkspaceSnapshot atRunTime = Snapshot(("a.cs", 1));
    WorkspaceSnapshot afterNoOp = Snapshot(("a.cs", 1));

    CompletionGateVerdict verdict = await Gate(afterNoOp)
        .EvaluateAsync(Root, true, Record(atRunTime.Digest, passed: true),
            TestContext.Current.CancellationToken);

    Assert.Equal(GoalGateStatus.Fresh, verdict.Status);
  }

  // Unverifiable never blocks: refusing here would trap the user with no exit, since
  // re-running the gate cannot fix a record that carries no digest.
  [Fact]
  public async Task RecordWithoutDigest_IsUnverifiableAndDoesNotBlock()
  {
    CompletionGateVerdict verdict = await Gate(Snapshot(("a.cs", 1)))
        .EvaluateAsync(Root, true, Record(digest: null, passed: true),
            TestContext.Current.CancellationToken);

    Assert.Equal(GoalGateStatus.Unverifiable, verdict.Status);
    Assert.False(verdict.BlocksCompletion);
  }

  [Fact]
  public async Task TruncatedWalk_IsUnverifiableAndDoesNotBlock()
  {
    WorkspaceSnapshot truncated = WorkspaceSnapshot.Create(Root,
        new Dictionary<string, WorkspaceFileFingerprint>(StringComparer.OrdinalIgnoreCase)
        {
          ["a.cs"] = new(10, DateTime.UtcNow, 1),
        }, isComplete: false);

    CompletionGateVerdict verdict = await Gate(truncated)
        .EvaluateAsync(Root, true, Record("wsanything", passed: true),
            TestContext.Current.CancellationToken);

    Assert.Equal(GoalGateStatus.Unverifiable, verdict.Status);
    Assert.False(verdict.BlocksCompletion);
  }

  // A corrupt record is not evidence of anything, so it cannot license a claim.
  [Fact]
  public async Task CorruptRecord_Blocks()
  {
    CompletionGateVerdict verdict = await Gate(Snapshot(("a.cs", 1)))
        .EvaluateAsync(Root, true, "not json at all",
            TestContext.Current.CancellationToken);

    Assert.Equal(GoalGateStatus.NeverRan, verdict.Status);
    Assert.True(verdict.BlocksCompletion);
  }

  [Fact]
  public void ParseRecord_RoundTrips()
  {
    GoalGateRecord? parsed = CompletionGate.ParseRecord(
        Record("wsabc", passed: true));

    Assert.NotNull(parsed);
    Assert.Equal("wsabc", parsed.Digest);
    Assert.True(parsed.Passed);
  }

  [Fact]
  public void ParseRecord_RejectsMissingPassed() =>
      Assert.Null(CompletionGate.ParseRecord(
          /*lang=json,strict*/"{\"digest\":\"x\",\"ranAt\":\"2026-01-01T00:00:00Z\"}"));

  private sealed class FixedSnapshotAccess(WorkspaceSnapshot snapshot) : IWorkspaceSnapshotAccess
  {
    public Task<WorkspaceSnapshot> CaptureAsync(string workspaceRoot, CancellationToken ct = default) =>
        Task.FromResult(snapshot);
  }
}
