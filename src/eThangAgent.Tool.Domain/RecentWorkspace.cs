using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>One directory the launcher lists, with the moment it was last opened. The root is
///     already normalized by <see cref="WorkspaceIdentity.Normalize"/>, so two spellings of one
///     directory are one row.</summary>
/// <param name="Root">The absolute directory path, normalized.</param>
/// <param name="OpenedAt">When the directory was last opened.</param>
public sealed record RecentWorkspace(string Root, DateTimeOffset OpenedAt);

/// <summary>Persistence port for the launcher's recent-workspace list.
///     <para>THE LIST IS THE WHOLE RECORD: a workspace IS its normalized directory path, so
///     there is no identity to mint and no marker file to write into the user's directory. A
///     moved directory therefore arrives as a new entry while the old one stops existing on
///     disk, which is what the launcher reports rather than repairing.</para>
///     <para>Expected failures flow as <see cref="DomainError"/>s through <see cref="Result{T}"/>
///     — never exceptions.</para></summary>
public interface IRecentWorkspaceStore
{
  /// <summary>The most recently opened directories, newest first, at most
  ///     <paramref name="limit"/> of them.</summary>
  /// <param name="limit">How many rows to return; must be at least 1.</param>
  /// <param name="ct">Cancellation for the read.</param>
  Task<Result<IReadOnlyList<RecentWorkspace>>> ListAsync(int limit, CancellationToken ct = default);

  /// <summary>Records that a directory was opened, replacing the timestamp of an existing row
  ///     rather than adding a second one.</summary>
  /// <param name="root">The directory that was opened.</param>
  /// <param name="ct">Cancellation for the write.</param>
  Task<Result<bool>> RecordAsync(string root, CancellationToken ct = default);

  /// <summary>Removes a directory from the list. Succeeds when no row existed.</summary>
  /// <param name="root">The directory to forget.</param>
  /// <param name="ct">Cancellation for the write.</param>
  Task<Result<bool>> ForgetAsync(string root, CancellationToken ct = default);
}
