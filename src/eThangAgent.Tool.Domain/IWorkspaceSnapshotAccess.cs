namespace eThangAgent.ToolDomain;

/// <summary>Captures the content-true state of a workspace tree. The seam exists for
///     the same reason every other file seam does: the domain asks for a capability,
///     and the implementation owns the technology.
///     <para>Implementations must never throw. An unreadable root, an unreadable
///     subdirectory, or a tree past <see cref="WorkspaceSnapshotLimits.MaxFiles"/>
///     yields a snapshot marked incomplete — the caller reports what it could see
///     instead of failing the run that produced it.</para></summary>
public interface IWorkspaceSnapshotAccess
{
  /// <summary>Walks <paramref name="workspaceRoot"/> and fingerprints every file the
  ///     report cares about. <paramref name="workspaceRoot"/> is the session workspace
  ///     or a child agent's anchor — never the process working directory.</summary>
  Task<WorkspaceSnapshot> CaptureAsync(string workspaceRoot, CancellationToken ct = default);

  /// <summary>The same walk, told what the tree looked like last time so an implementation may
  ///     reuse evidence it already holds for a file that has not moved. The answer describes the
  ///     tree exactly as <see cref="CaptureAsync(string, CancellationToken)"/> would.
  ///     <para>An implementation without a cheaper path repeats the walk, so the overload is
  ///     correct by default. <paramref name="previous"/> null means no earlier evidence exists.
  ///     </para></summary>
  Task<WorkspaceSnapshot> CaptureAsync(string workspaceRoot, WorkspaceSnapshot? previous,
      CancellationToken ct = default) => CaptureAsync(workspaceRoot, ct);
}
