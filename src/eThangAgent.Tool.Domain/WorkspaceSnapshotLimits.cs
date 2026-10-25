namespace eThangAgent.ToolDomain;

/// <summary>The bounds a workspace snapshot runs under. Both are load-bearing: a
///     snapshot is taken on the exec path, so it may never walk an unbounded tree or
///     read an unbounded file.
///     <para>A file above <see cref="MaxHashedBytes"/> is still snapshotted, but by
///     metadata only — its content hash is null and comparisons fall back to
///     (length, last-write). Hashing a multi-hundred-megabyte artifact twice per exec
///     call would cost more than the report is worth, and the honest degradation beats
///     an unusable tool.</para>
///     <para>A tree above <see cref="MaxFiles"/> yields an INCOMPLETE snapshot. An
///     incomplete snapshot never silently claims completeness: the change report says
///     the walk was truncated, and the completion gate treats the digest as
///     unverifiable rather than fresh.</para></summary>
public static class WorkspaceSnapshotLimits
{
  /// <summary>Largest file whose content is hashed. Measured on this workspace: 1,834
  ///     files totalling 35 MB hash in ~106 ms, against a 1.03 GB tree whose large
  ///     artifacts are skipped by this cap.</summary>
  public const long MaxHashedBytes = 1024 * 1024;

  /// <summary>Most files a snapshot will record before declaring itself incomplete.</summary>
  public const int MaxFiles = 100_000;
}
