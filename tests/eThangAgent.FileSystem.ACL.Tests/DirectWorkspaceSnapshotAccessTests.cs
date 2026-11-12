using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

// Issue #283 Part 1 against the REAL file system: the defect is a read-modify-write whose
// anchor did not match, so the file is written back byte-identical. A metadata-only
// comparison calls that a change; the content hash is the only evidence that does not.
public class DirectWorkspaceSnapshotAccessTests
{
  private static string NewTree() => TestTempRoot.CreateDirectory("wssnap").FullName;

  private static void Write(string root, string relative, string content)
  {
    string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content);
  }

  [Fact]
  public async Task Capture_RecordsWorkspaceRelativePathsWithForwardSlashes()
  {
    string root = NewTree();
    try
    {
      Write(root, "src/deep/a.cs", "one");

      WorkspaceSnapshot snapshot = await new DirectWorkspaceSnapshotAccess()
          .CaptureAsync(root, TestContext.Current.CancellationToken);

      Assert.True(snapshot.IsComplete);
      Assert.Contains("src/deep/a.cs", snapshot.Files.Keys);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  // THE REGRESSION THIS FEATURE EXISTS FOR. The timestamp moves; the report must not.
  [Fact]
  public async Task Capture_NoOpRewrite_IsNotReportedAsAChange()
  {
    string root = NewTree();
    try
    {
      Write(root, "AgentComposition.cs", "// registration\nvar x = 1;\n");
      DirectWorkspaceSnapshotAccess access = new();
      WorkspaceSnapshot before = await access.CaptureAsync(root, TestContext.Current.CancellationToken);

      // Exactly the session's slip: replace against an anchor typed from memory.
      string path = Path.Combine(root, "AgentComposition.cs");
      string text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
      text = text.Replace("anchor-typed-from-memory", "replacement", StringComparison.Ordinal);
      await File.WriteAllTextAsync(path, text, TestContext.Current.CancellationToken);

      WorkspaceSnapshot after = await access.CaptureAsync(root, TestContext.Current.CancellationToken);
      WorkspaceChangeSet changes = before.DiffTo(after);

      Assert.True(changes.IsEmpty, "a byte-identical rewrite must report nothing changed");
      Assert.Equal("[exec: modified 0 workspace files — the program completed but changed nothing]",
          changes.Render());
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  [Fact]
  public async Task Capture_RealEdit_IsReportedAsModified()
  {
    string root = NewTree();
    try
    {
      Write(root, "a.cs", "one");
      DirectWorkspaceSnapshotAccess access = new();
      WorkspaceSnapshot before = await access.CaptureAsync(root, TestContext.Current.CancellationToken);

      Write(root, "a.cs", "two");

      WorkspaceChangeSet changes = before.DiffTo(
          await access.CaptureAsync(root, TestContext.Current.CancellationToken));

      Assert.Equal(["a.cs"], changes.Modified);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  [Fact]
  public async Task Capture_CreatedAndDeleted_AreReported()
  {
    string root = NewTree();
    try
    {
      Write(root, "gone.cs", "x");
      DirectWorkspaceSnapshotAccess access = new();
      WorkspaceSnapshot before = await access.CaptureAsync(root, TestContext.Current.CancellationToken);

      File.Delete(Path.Combine(root, "gone.cs"));
      Write(root, "new.cs", "y");

      WorkspaceChangeSet changes = before.DiffTo(
          await access.CaptureAsync(root, TestContext.Current.CancellationToken));

      Assert.Equal(["new.cs"], changes.Added);
      Assert.Equal(["gone.cs"], changes.Removed);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  // Exclusions are load-bearing: a build inside an exec script writes thousands of files
  // under bin/obj, and reporting them would drown the one file the model edited.
  [Fact]
  public async Task Capture_SkipsBuildOutputAndVcsInternals()
  {
    string root = NewTree();
    try
    {
      Write(root, "src/a.cs", "one");
      Write(root, "bin/Debug/b.dll", "binary");
      Write(root, "obj/project.assets.json", "{}");
      Write(root, ".git/HEAD", "ref");
      Write(root, "node_modules/pkg/index.js", "js");

      WorkspaceSnapshot snapshot = await new DirectWorkspaceSnapshotAccess()
          .CaptureAsync(root, TestContext.Current.CancellationToken);

      Assert.Equal(["src/a.cs"], [.. snapshot.Files.Keys]);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  // Segment matching: 'obj' must not exclude a directory that merely shares the prefix.
  [Fact]
  public async Task Capture_DoesNotExcludeDirectoriesSharingAPrefix()
  {
    string root = NewTree();
    try
    {
      Write(root, "objects/model.cs", "kept");

      WorkspaceSnapshot snapshot = await new DirectWorkspaceSnapshotAccess()
          .CaptureAsync(root, TestContext.Current.CancellationToken);

      Assert.Contains("objects/model.cs", snapshot.Files.Keys);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  /// <summary>A pass over an unchanged tree reuses the evidence it already holds instead of
  ///     re-reading every file, and the answer is identical to a cold walk: same files, same
  ///     fingerprints, same digest.</summary>
  [Fact]
  public async Task Capture_WithUnchangedPrevious_ProducesTheSameSnapshot()
  {
    string root = NewTree();
    try
    {
      Write(root, "a.cs", "one");
      Write(root, "src/b.cs", "two");
      DirectWorkspaceSnapshotAccess access = new();
      WorkspaceSnapshot cold = await access.CaptureAsync(root, TestContext.Current.CancellationToken);

      WorkspaceSnapshot warm = await access.CaptureAsync(root, cold, TestContext.Current.CancellationToken);

      Assert.Equal(cold.Digest, warm.Digest);
      Assert.Equal(cold.Files.Count, warm.Files.Count);
      foreach ((string path, WorkspaceFileFingerprint fingerprint) in cold.Files)
      {
        Assert.Equal(fingerprint, warm.Files[path]);
      }
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  /// <summary>A file whose metadata moved is read again, so a same-length in-place edit is
  ///     still reported against a warm previous.
  ///     <para>The last-write time is set explicitly because both versions would otherwise land
  ///     inside one filesystem timestamp tick, making 'unchanged metadata' and 'changed content'
  ///     the same observation.</para></summary>
  [Fact]
  public async Task Capture_WithPrevious_StillSeesASameLengthEdit()
  {
    string root = NewTree();
    try
    {
      Write(root, "a.cs", "abc");
      string path = Path.Combine(root, "a.cs");
      File.SetLastWriteTimeUtc(path, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
      DirectWorkspaceSnapshotAccess access = new();
      WorkspaceSnapshot before = await access.CaptureAsync(root, TestContext.Current.CancellationToken);

      Write(root, "a.cs", "xyz");
      File.SetLastWriteTimeUtc(path, new DateTime(2024, 1, 1, 0, 0, 1, DateTimeKind.Utc));

      WorkspaceSnapshot after = await access.CaptureAsync(root, before, TestContext.Current.CancellationToken);

      Assert.Equal(["a.cs"], before.DiffTo(after).Modified);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  /// <summary>A no-op rewrite moves the timestamp but not the bytes, so the file is re-read and
  ///     the report stays empty.</summary>
  [Fact]
  public async Task Capture_WithPrevious_StillIgnoresANoOpRewrite()
  {
    string root = NewTree();
    try
    {
      Write(root, "a.cs", "abc");
      string path = Path.Combine(root, "a.cs");
      File.SetLastWriteTimeUtc(path, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
      DirectWorkspaceSnapshotAccess access = new();
      WorkspaceSnapshot before = await access.CaptureAsync(root, TestContext.Current.CancellationToken);

      Write(root, "a.cs", "abc");
      File.SetLastWriteTimeUtc(path, new DateTime(2024, 1, 1, 0, 0, 2, DateTimeKind.Utc));

      WorkspaceSnapshot after = await access.CaptureAsync(root, before, TestContext.Current.CancellationToken);

      Assert.True(before.DiffTo(after).IsEmpty, "a byte-identical rewrite is not a change");
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  /// <summary>When a file's length and last-write time are unchanged, the recorded hash is
  ///     reused rather than the file being read again.
  ///     <para>The content is written behind the metadata's back here, which no real writer can
  ///     do, so the assertion is about the reuse rather than about what a user would see.
  ///     </para></summary>
  [Fact]
  public async Task Capture_WithUnchangedMetadata_ReusesTheRecordedHash()
  {
    string root = NewTree();
    try
    {
      Write(root, "a.cs", "abc");
      string path = Path.Combine(root, "a.cs");
      DateTime stamp = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
      File.SetLastWriteTimeUtc(path, stamp);
      DirectWorkspaceSnapshotAccess access = new();
      WorkspaceSnapshot before = await access.CaptureAsync(root, TestContext.Current.CancellationToken);
      ulong recorded = Assert.NotNull(before.Files["a.cs"].ContentHash);

      // Same length, same last-write time: the metadata says nothing has been written since.
      await File.WriteAllTextAsync(path, "xyz", TestContext.Current.CancellationToken);
      File.SetLastWriteTimeUtc(path, stamp);

      WorkspaceSnapshot after = await access.CaptureAsync(root, before, TestContext.Current.CancellationToken);

      Assert.Equal(recorded, after.Files["a.cs"].ContentHash);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  /// <summary>A previous snapshot of a DIFFERENT tree is not evidence about this one.</summary>
  [Fact]
  public async Task Capture_WithPreviousFromAnotherRoot_IgnoresIt()
  {
    string one = NewTree();
    string two = NewTree();
    try
    {
      Write(one, "a.cs", "one");
      Write(two, "a.cs", "two");
      DirectWorkspaceSnapshotAccess access = new();
      WorkspaceSnapshot other = await access.CaptureAsync(one, TestContext.Current.CancellationToken);

      WorkspaceSnapshot here = await access.CaptureAsync(two, other, TestContext.Current.CancellationToken);

      // The other tree's evidence was not applied: this tree's own content is what the
      // snapshot describes, so a cold walk of it agrees exactly.
      Assert.Equal(here.Digest, (await access.CaptureAsync(two, TestContext.Current.CancellationToken)).Digest);
      Assert.Equal("two", await File.ReadAllTextAsync(Path.Combine(two, "a.cs"),
          TestContext.Current.CancellationToken));
    }
    finally
    {
      Directory.Delete(one, true);
      Directory.Delete(two, true);
    }
  }

  [Fact]
  public async Task Capture_MissingRoot_IsIncompleteNotThrowing()
  {
    WorkspaceSnapshot snapshot = await new DirectWorkspaceSnapshotAccess()
        .CaptureAsync(Path.Combine(TestTempRoot.Root, "definitely-missing-" + Guid.NewGuid()),
            TestContext.Current.CancellationToken);

    Assert.False(snapshot.IsComplete);
  }

  // Content, not size: a same-length in-place edit is a change.
  [Fact]
  public async Task Capture_SameLengthDifferentContent_IsAChange()
  {
    string root = NewTree();
    try
    {
      Write(root, "a.cs", "abc");
      DirectWorkspaceSnapshotAccess access = new();
      WorkspaceSnapshot before = await access.CaptureAsync(root, TestContext.Current.CancellationToken);

      Write(root, "a.cs", "xyz");

      WorkspaceChangeSet changes = before.DiffTo(
          await access.CaptureAsync(root, TestContext.Current.CancellationToken));

      Assert.Equal(["a.cs"], changes.Modified);
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }
}
