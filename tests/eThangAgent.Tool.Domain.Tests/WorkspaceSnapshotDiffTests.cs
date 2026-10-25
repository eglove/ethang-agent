namespace eThangAgent.ToolDomain.Tests;

// Issue #283 Part 1: the change report is a FACT, so the diff must distinguish a write
// that changed nothing from one that changed content. Every test here is about that
// distinction - the whole feature is worthless if it cannot make it.
public class WorkspaceSnapshotDiffTests
{
  private static readonly DateTimeOffset Base = new(2026, 10, 25, 10, 0, 0, TimeSpan.Zero);

  private static WorkspaceSnapshot Snapshot(params (string Path, ulong Hash)[] files) =>
      WorkspaceSnapshot.Create("C:\\ws", files.ToDictionary(
          f => f.Path,
          f => new WorkspaceFileFingerprint(10, Base.UtcDateTime, f.Hash),
          StringComparer.OrdinalIgnoreCase));

  [Fact]
  public void Diff_IdenticalContent_ReportsNothingChanged()
  {
    WorkspaceChangeSet changes = Snapshot(("src/a.cs", 1)).DiffTo(Snapshot(("src/a.cs", 1)));

    Assert.True(changes.IsEmpty);
    Assert.Equal(0, changes.Count);
  }

  // THE CASE THE FEATURE EXISTS FOR: read the file, replace against an anchor that does
  // not match, write the same bytes back. The last-write time moves; the content does
  // not. A metadata comparison reports a change here and the report becomes a lie.
  [Fact]
  public void Diff_SameContentNewerTimestamp_IsNotAChange()
  {
    WorkspaceSnapshot before = WorkspaceSnapshot.Create("C:\\ws",
        new Dictionary<string, WorkspaceFileFingerprint>(StringComparer.OrdinalIgnoreCase)
        {
          ["src/a.cs"] = new(10, Base.UtcDateTime, 42),
        });
    WorkspaceSnapshot after = WorkspaceSnapshot.Create("C:\\ws",
        new Dictionary<string, WorkspaceFileFingerprint>(StringComparer.OrdinalIgnoreCase)
        {
          ["src/a.cs"] = new(10, Base.AddMinutes(5).UtcDateTime, 42),
        });

    WorkspaceChangeSet changes = before.DiffTo(after);

    Assert.True(changes.IsEmpty);
    Assert.Contains("modified 0 workspace files", changes.Render(), StringComparison.Ordinal);
  }

  [Fact]
  public void Diff_ChangedContent_ReportsModified()
  {
    WorkspaceChangeSet changes = Snapshot(("src/a.cs", 1)).DiffTo(Snapshot(("src/a.cs", 2)));

    Assert.Equal(["src/a.cs"], changes.Modified);
    Assert.Empty(changes.Added);
    Assert.Empty(changes.Removed);
  }

  [Fact]
  public void Diff_NewFile_ReportsAdded()
  {
    WorkspaceChangeSet changes = Snapshot().DiffTo(Snapshot(("src/new.cs", 1)));

    Assert.Equal(["src/new.cs"], changes.Added);
  }

  [Fact]
  public void Diff_DeletedFile_ReportsRemoved()
  {
    WorkspaceChangeSet changes = Snapshot(("src/gone.cs", 1)).DiffTo(Snapshot());

    Assert.Equal(["src/gone.cs"], changes.Removed);
  }

  // Files above the hashing cap carry no content hash. The comparison degrades to the
  // metadata pair - documented, and the honest answer for a file too large to hash.
  [Fact]
  public void Diff_UnhashedFile_FallsBackToMetadata()
  {
    Dictionary<string, WorkspaceFileFingerprint> before = new(StringComparer.OrdinalIgnoreCase)
    {
      ["big.bin"] = new(1000, Base.UtcDateTime, null),
    };
    Dictionary<string, WorkspaceFileFingerprint> after = new(StringComparer.OrdinalIgnoreCase)
    {
      ["big.bin"] = new(2000, Base.AddMinutes(1).UtcDateTime, null),
    };

    WorkspaceChangeSet changes = WorkspaceSnapshot.Create("C:\\ws", before)
        .DiffTo(WorkspaceSnapshot.Create("C:\\ws", after));

    Assert.Equal(["big.bin"], changes.Modified);
  }

  [Fact]
  public void Digest_IsStableAcrossInsertionOrder()
  {
    WorkspaceSnapshot a = Snapshot(("a.cs", 1), ("b.cs", 2));
    WorkspaceSnapshot b = Snapshot(("b.cs", 2), ("a.cs", 1));

    Assert.Equal(a.Digest, b.Digest);
  }

  [Fact]
  public void Digest_DiffersWhenContentDiffers() =>
      Assert.NotEqual(Snapshot(("a.cs", 1)).Digest, Snapshot(("a.cs", 2)).Digest);

  // A truncated walk must never collide with a complete one holding the same files:
  // 'I could not see everything' is a different fact from 'everything is this'.
  [Fact]
  public void Digest_DistinguishesTruncatedFromComplete()
  {
    Dictionary<string, WorkspaceFileFingerprint> files = new(StringComparer.OrdinalIgnoreCase)
    {
      ["a.cs"] = new(10, Base.UtcDateTime, 1),
    };

    Assert.NotEqual(
        WorkspaceSnapshot.Create("C:\\ws", files, isComplete: true).Digest,
        WorkspaceSnapshot.Create("C:\\ws", files, isComplete: false).Digest);
  }

  [Fact]
  public void Unavailable_IsIncompleteAndEmpty()
  {
    WorkspaceSnapshot unavailable = WorkspaceSnapshot.Unavailable("C:\\missing");

    Assert.False(unavailable.IsComplete);
    Assert.Empty(unavailable.Files);
  }
}
