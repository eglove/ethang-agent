using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

// Issue #283 Part 1 against the REAL file system: the defect is a read-modify-write whose
// anchor did not match, so the file is written back byte-identical. A metadata-only
// comparison calls that a change; the content hash is the only evidence that does not.
public class DirectWorkspaceSnapshotAccessTests
{
  private static string NewTree() => Directory.CreateTempSubdirectory("wssnap").FullName;

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

  [Fact]
  public async Task Capture_MissingRoot_IsIncompleteNotThrowing()
  {
    WorkspaceSnapshot snapshot = await new DirectWorkspaceSnapshotAccess()
        .CaptureAsync(Path.Combine(Path.GetTempPath(), "definitely-missing-" + Guid.NewGuid()),
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
