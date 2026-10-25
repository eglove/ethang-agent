using System.Globalization;

namespace eThangAgent.ToolDomain;

/// <summary>One content-true picture of a workspace tree, keyed by workspace-relative
///     path (always '/'-separated, so a report reads the same on every platform).
///     <para><see cref="Digest"/> is the tree's identity: two snapshots with the same
///     digest hold identical content, whatever their timestamps say. That is what makes
///     the completion gate's freshness question answerable — 'has anything changed since
///     the gate ran' is a digest comparison, not a clock comparison.</para>
///     <para><see cref="IsComplete"/> is false when the walk was truncated (the tree
///     exceeded <see cref="WorkspaceSnapshotLimits.MaxFiles"/>) or the root could not be
///     read at all. An incomplete snapshot is still useful for a diff, but it must never
///     be reported as a complete account of the tree.</para></summary>
public sealed class WorkspaceSnapshot
{
  private WorkspaceSnapshot(string root,
      IReadOnlyDictionary<string, WorkspaceFileFingerprint> files, bool isComplete)
  {
    Root = root;
    Files = files;
    IsComplete = isComplete;
    Digest = ComputeDigest(files, isComplete);
  }

  /// <summary>The absolute root this snapshot describes.</summary>
  public string Root { get; }

  /// <summary>Every captured file, keyed by '/'-separated workspace-relative path.</summary>
  public IReadOnlyDictionary<string, WorkspaceFileFingerprint> Files { get; }

  /// <summary>False when the walk could not see the whole tree.</summary>
  public bool IsComplete { get; }

  /// <summary>Stable identity of the tree's content. Equal digests mean equal content.
  ///     An incomplete snapshot's digest carries the incompleteness itself, so a
  ///     truncated walk can never collide with a complete one that happens to hold the
  ///     same files.</summary>
  public string Digest { get; }

  /// <summary>Builds a snapshot from an already-collected file set.</summary>
  public static WorkspaceSnapshot Create(string root,
      IReadOnlyDictionary<string, WorkspaceFileFingerprint> files, bool isComplete = true)
  {
    ArgumentNullException.ThrowIfNull(root);
    ArgumentNullException.ThrowIfNull(files);
    return new WorkspaceSnapshot(root, files, isComplete);
  }

  /// <summary>An empty, incomplete snapshot: what a caller uses when the tree could not
  ///     be read at all. Incomplete by construction — an unreadable tree is not an empty
  ///     one, and the two must never be confused.</summary>
  public static WorkspaceSnapshot Unavailable(string root) =>
      new(root, new Dictionary<string, WorkspaceFileFingerprint>(StringComparer.OrdinalIgnoreCase),
          isComplete: false);

  /// <summary>Computes what changed between this snapshot (the BEFORE) and
  ///     <paramref name="after"/> (the AFTER).</summary>
  public WorkspaceChangeSet DiffTo(WorkspaceSnapshot after)
  {
    ArgumentNullException.ThrowIfNull(after);
    List<string> added = [];
    List<string> modified = [];
    List<string> removed = [];

    foreach ((string path, WorkspaceFileFingerprint now) in after.Files)
    {
      if (!Files.TryGetValue(path, out WorkspaceFileFingerprint before))
      {
        added.Add(path);
      }
      else if (!before.SameContentAs(now))
      {
        modified.Add(path);
      }
    }

    removed.AddRange(Files.Keys.Where(path => !after.Files.ContainsKey(path)));

    added.Sort(StringComparer.Ordinal);
    modified.Sort(StringComparer.Ordinal);
    removed.Sort(StringComparer.Ordinal);
    return new WorkspaceChangeSet(added, modified, removed,
        IsComplete && after.IsComplete);
  }

  /// <summary>A digest over the sorted (path, content) pairs. Sorted so the walk order
  ///     can never change the identity; content-hashed so a rewrite that changed nothing
  ///     cannot change it either.</summary>
  private static string ComputeDigest(
      IReadOnlyDictionary<string, WorkspaceFileFingerprint> files, bool isComplete)
  {
    ulong h = 14695981039346656037UL; // FNV-1a offset basis
    foreach ((string path, WorkspaceFileFingerprint file) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
    {
      h = Mix(h, path);
      // A file whose content was not hashed contributes its metadata instead — the
      // documented degradation, and it must be distinguishable from a hashed file with
      // the same path, so the marker rides the digest.
      h = file.ContentHash is { } content
          ? Mix(h, "c" + content.ToString("x16", CultureInfo.InvariantCulture))
          : Mix(h, "m" + file.Length.ToString(CultureInfo.InvariantCulture)
              + ":" + file.LastWriteUtc.Ticks.ToString(CultureInfo.InvariantCulture));
    }

    h = Mix(h, isComplete ? "complete" : "truncated");
    return "ws" + h.ToString("x16", CultureInfo.InvariantCulture);
  }

  private static ulong Mix(ulong h, string text)
  {
    foreach (char c in text)
    {
      h = (h ^ c) * 1099511628211UL;
    }

    return (h ^ '/') * 1099511628211UL;
  }

  /// <summary>Renders the file count for diagnostics and tests.</summary>
  public override string ToString() =>
      string.Create(CultureInfo.InvariantCulture,
          $"workspace snapshot {Digest} ({Files.Count} file(s){(IsComplete ? string.Empty : ", truncated")})");
}
