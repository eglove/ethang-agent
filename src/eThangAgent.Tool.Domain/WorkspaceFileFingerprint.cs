namespace eThangAgent.ToolDomain;

/// <summary>What one workspace file looked like at a snapshot moment: its size, its
///     last-write time, and — for files small enough to hash cheaply — a content hash.
///     <para>CONTENT IS THE JUDGE; metadata is the fallback. A write that changes nothing
///     (read the file, replace against an anchor that does not match, write the same
///     bytes back) updates the last-write time but not the content, so a metadata-only
///     comparison reports a change that never happened — the exact false report issue
///     #283 exists to eliminate. When the content hash is unavailable (the file exceeds
///     <see cref="WorkspaceSnapshotLimits.MaxHashedBytes"/>) the metadata pair is all the
///     evidence there is, and the comparison degrades to it honestly rather than
///     claiming a certainty it does not have.</para></summary>
public readonly record struct WorkspaceFileFingerprint(
    long Length,
    DateTime LastWriteUtc,
    ulong? ContentHash)
{
  /// <summary>True when this fingerprint carries a content hash, so equality can be
  ///     decided on content alone.</summary>
  public bool ContentKnown => ContentHash.HasValue;

  /// <summary>True when the two fingerprints describe the SAME content. Both hashes
  ///     present: the hashes decide. Otherwise the metadata pair decides, which is the
  ///     documented degradation for files too large to hash.</summary>
  public bool SameContentAs(WorkspaceFileFingerprint other) =>
      ContentHash is { } mine && other.ContentHash is { } theirs
          ? mine == theirs
          : Length == other.Length && LastWriteUtc == other.LastWriteUtc;
}
