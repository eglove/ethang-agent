using System.Security.Cryptography;
using eThangAgent.ToolDomain;

// Named decision (CA1849): a workspace snapshot is local-disk enumeration and small-file
// hashing. Sync APIs inside the async wrapper keep the walk simple without meaningful
// thread blocking - the exec path already runs off the UI thread, and the measured cost
// on this workspace (1,850 files, 35 MB hashed) is ~135 ms.
#pragma warning disable CA1849 // Call async methods when in an async method
namespace eThangAgent.FileSystem.ACL;

/// <summary>Content-true workspace snapshots over the local file system.
///     <para>WHY CONTENT AND NOT METADATA: the defect this exists to catch is a
///     read-modify-write whose anchor did not match - the file is written back
///     byte-identical, so its last-write time moves and its content does not. A
///     metadata-only comparison calls that a change and the report becomes a confident
///     lie. Content hashing is the only evidence that distinguishes the two.</para>
///     <para>EXCLUSIONS are load-bearing rather than an optimisation, for the same reason
///     the Git view excludes them: a <c>dotnet build</c> inside an exec script writes
///     thousands of files under <c>bin</c>/<c>obj</c>, and without pruning the report
///     would drown the one file the model actually edited. The trade is deliberate and
///     documented - a write INTO a build-output directory is not reported.</para>
///     <para>Matched as whole path SEGMENTS, case-insensitively, so a directory called
///     <c>objects</c> is never excluded by <c>obj</c>.</para></summary>
public sealed class DirectWorkspaceSnapshotAccess : IWorkspaceSnapshotAccess
{
  /// <summary>Directory names never walked into. Build output, VCS internals,
  ///     dependency trees, and tool caches - the trees that change by the thousand
  ///     without the model having touched anything.</summary>
  internal static readonly IReadOnlyList<string> ExcludedDirectories =
  [
    ".git", ".svn", ".hg",
    "bin", "obj", "node_modules", "packages",
    ".worktrees", ".venv", "venv", "__pycache__",
    ".idea", ".vs", ".vscode", ".nuget",
  ];

  /// <inheritdoc />
  public Task<WorkspaceSnapshot> CaptureAsync(string workspaceRoot, CancellationToken ct = default)
  {
    if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
    {
      // An unreadable tree is not an empty one (see WorkspaceSnapshot.Unavailable).
      return Task.FromResult(WorkspaceSnapshot.Unavailable(workspaceRoot ?? string.Empty));
    }

    Dictionary<string, WorkspaceFileFingerprint> files = new(StringComparer.OrdinalIgnoreCase);
    bool complete = Walk(workspaceRoot, workspaceRoot, files, ct);
    return Task.FromResult(WorkspaceSnapshot.Create(workspaceRoot, files, complete));
  }

  /// <summary>Depth-first walk with segment-aware pruning. Returns false when the walk
  ///     stopped early - the file cap was reached or a subtree could not be read - so the
  ///     snapshot never claims to be complete when it is not.</summary>
  private static bool Walk(string root, string directory,
      Dictionary<string, WorkspaceFileFingerprint> files, CancellationToken ct)
  {
    ct.ThrowIfCancellationRequested();
    bool complete = true;

    IEnumerable<string> entries;
    try
    {
      entries = Directory.EnumerateFiles(directory);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
      return false;
    }

    foreach (string file in entries)
    {
      if (files.Count >= WorkspaceSnapshotLimits.MaxFiles)
      {
        return false;
      }

      if (Fingerprint(file) is { } fingerprint)
      {
        files[Relative(root, file)] = fingerprint;
      }
    }

    IEnumerable<string> subdirectories;
    try
    {
      subdirectories = Directory.EnumerateDirectories(directory);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
      return false;
    }

    foreach (string child in subdirectories)
    {
      if (ExcludedDirectories.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase))
      {
        continue;
      }

      complete &= Walk(root, child, files, ct);
    }

    return complete;
  }

  /// <summary>One file's fingerprint, or null when it vanished mid-walk or could not be
  ///     read. A file that disappears between enumeration and stat is an ordinary race
  ///     (a build cleaning up after itself), not a failure.</summary>
  private static WorkspaceFileFingerprint? Fingerprint(string path)
  {
    try
    {
      FileInfo info = new(path);
      if (!info.Exists)
      {
        return null;
      }

      long length = info.Length;
      DateTime lastWrite = info.LastWriteTimeUtc;
      ulong? hash = length <= WorkspaceSnapshotLimits.MaxHashedBytes ? Hash(path) : null;
      return new WorkspaceFileFingerprint(length, lastWrite, hash);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
      return null;
    }
  }

  /// <summary>The file's content hash, or null when it cannot be read. SHA-256 truncated
  ///     to 64 bits: this answers 'is this the same content', not 'is this file
  ///     authentic', so collision resistance beyond 64 bits buys nothing a change report
  ///     can use, and the shorter digest keeps the tree digest cheap to fold.</summary>
  private static ulong? Hash(string path)
  {
    try
    {
      using FileStream stream = File.OpenRead(path);
      Span<byte> digest = stackalloc byte[32];
      _ = SHA256.HashData(stream, digest);
      return BitConverter.ToUInt64(digest[..8]);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
      return null;
    }
  }

  /// <summary>The '/'-separated workspace-relative path. Always '/', so a report reads
  ///     the same whatever separator the platform produced.</summary>
  private static string Relative(string root, string path) =>
      Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
}
