namespace eThangAgent.ToolDomain;

/// <summary>One watcher announcement (issue #350): the paths that changed in a pass,
///     the paths that were deleted, the generation that pass advanced to, and when.
///     Paths are '/'-separated workspace-relative, sorted, matching WorkspaceSnapshot keys.</summary>
public sealed record FileChangeAnnouncement(
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> DeletedPaths,
    long Generation,
    DateTimeOffset UtcTimestamp);
