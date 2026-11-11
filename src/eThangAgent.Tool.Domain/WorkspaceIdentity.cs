using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>One binding between a workspace's identity and the directory path that identity
///     IS.</summary>
/// <param name="Id">The workspace identity: the normalized directory path.</param>
/// <param name="Root">The absolute directory path the identity is bound to.</param>
public sealed record WorkspaceIdentityBinding(string Id, string Root);

/// <summary>The workspace identity rule: a workspace IS its directory path.
///     <para>NOTHING IS WRITTEN INTO THE USER'S DIRECTORY to identify it. A marker file would
///     be an artifact on the user's machine outside the database, and the harness adds none.
///     The cost is accepted and stated: a directory that is MOVED or RENAMED arrives as a new
///     workspace, and the records scoped to the old path stay with the old path.</para></summary>
public static class WorkspaceIdentity

{
  /// <summary>The identity of the directory at <paramref name="root"/>: its normalized path.
  ///     <para>PURE: no file system, no database, no clock. The same directory always resolves
  ///     to the same identity, and two spellings of one directory (a trailing separator, a
  ///     different case on Windows) resolve to the same identity too.</para></summary>
  public static string For(string root)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(root);
    return Normalize(root);
  }

  /// <summary>Path comparison for workspace roots: case-insensitive and trailing-separator
  ///     blind, because Windows reports one directory both ways and a change of separator is
  ///     not a different workspace.</summary>
  public static bool PathsEqual(string left, string right)
      => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

  /// <summary>The comparable form of a workspace root: trimmed of trailing separators.
  ///     Deliberately NOT full-path canonicalized - the stores record what the caller opened,
  ///     and two spellings of one directory compare equal here without inventing a third
  ///     spelling to store.
  ///     <para>A ROOT KEEPS ONE SEPARATOR. <c>C:\</c> is the root of a drive and <c>C:</c> is a
  ///     relative path to its current directory, so trimming the last separator of a root would
  ///     mint a different directory rather than a shorter spelling of the same one.</para></summary>
  public static string Normalize(string path)
  {
    if (string.IsNullOrWhiteSpace(path))
    {
      return string.Empty;
    }

    string trimmed = path.Trim();
    string withoutSeparators = trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    return withoutSeparators.EndsWith(':', StringComparison.Ordinal) && trimmed.Length > withoutSeparators.Length
        ? withoutSeparators + trimmed[withoutSeparators.Length]
        : withoutSeparators;
  }

  /// <summary>The identity a durable store binds at construction: <paramref name="workspaceId"/>
  ///     when it is already normalized, and a refusal otherwise.
  ///     <para>REFUSING RATHER THAN NORMALIZING: an unnormalized id can only have been minted by
  ///     a caller that did not apply the rule, and a store that repaired it silently would leave
  ///     that caller writing rows under the other spelling while reading them under this one.
  ///     </para></summary>
  /// <exception cref="ArgumentException">The id is blank or is not already normalized.</exception>
  public static string Require(string workspaceId)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
    string normalized = Normalize(workspaceId);
    return string.Equals(workspaceId, normalized, StringComparison.Ordinal)
        ? workspaceId
        : throw new ArgumentException(
            $"the workspace id '{workspaceId}' is not a normalized workspace identity; the normalized form is '{normalized}'.",
            nameof(workspaceId));
  }
}

/// <summary>Persistence port for workspace identity bindings. The implementation lives in
///     the Storage ACL. Expected failures flow as <see cref="DomainError"/>s through
///     <see cref="Result{T}"/> — never exceptions.
///     <para>The table is how the orphan check answers 'does this chain's workspace still
///     exist', and how a session record's identity is compared to a directory on disk.</para>
///     </summary>
public interface IWorkspaceIdentityStore
{
  /// <summary>The binding for an identity, or null when nothing has bound it.</summary>
  Task<Result<WorkspaceIdentityBinding?>> FindByIdAsync(string id, CancellationToken ct = default);

  /// <summary>The binding for a directory path, or null when no identity claims it.</summary>
  Task<Result<WorkspaceIdentityBinding?>> FindByRootAsync(string root, CancellationToken ct = default);

  /// <summary>Binds an identity to a path, replacing any binding for EITHER side so two
  ///     identities never fight over one directory. Idempotent for an unchanged pair.</summary>
  Task<Result<WorkspaceIdentityBinding>> BindAsync(string id, string root, CancellationToken ct = default);

  /// <summary>Every binding, newest first - what the orphan check walks to answer 'does this
  ///     chain's workspace still exist'.</summary>
  Task<Result<IReadOnlyList<WorkspaceIdentityBinding>>> ListAsync(CancellationToken ct = default);
};
