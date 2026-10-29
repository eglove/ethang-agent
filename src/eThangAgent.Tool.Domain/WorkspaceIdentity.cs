using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>One binding between a workspace's STABLE identity and the directory path that
///     identity currently lives at (issue #317 D5). The id is opaque: a workspace opened
///     before #317 carries its path AS its id, and a workspace opened after carries a minted
///     id. Nothing may interpret an id — the identity table is the only thing that does.</summary>
/// <param name="Id">The stable workspace identity.</param>
/// <param name="Root">The absolute directory path the identity is bound to.</param>
public sealed record WorkspaceIdentityBinding(string Id, string Root);

/// <summary>What resolving a workspace's identity did, so the caller can tell an ordinary
///     open from a directory that moved or was copied.</summary>
public enum WorkspaceIdentityOutcome
{
  /// <summary>The directory's identity is what it was: a first open that bound it, or an
  ///     ordinary reopen. Nothing to tell the user.</summary>
  Bound,

  /// <summary>The directory's marker id was bound to a path that no longer exists, so this
  ///     is the SAME workspace at a new location (issue #317 D5: a move is RECOVERABLE).
  ///     The identity is re-bound to the new path and every record follows it.</summary>
  Moved,

  /// <summary>The directory's marker id was bound to a path that still exists, so this is a
  ///     COPY of that workspace (issue #317 D6). Two directories must never share one record
  ///     set, so the copy gets a FRESH identity and the user is told.</summary>
  Copied,
}

/// <summary>The outcome of resolving one directory's workspace identity, carrying the notice a
///     surface should show (null when there is nothing to say).</summary>
public sealed record WorkspaceIdentityResolution(string Id, WorkspaceIdentityOutcome Outcome, string? Notice)
{
  /// <summary>True when the resolution changed something the user should hear about.</summary>
  public bool IsNoteworthy => Outcome != WorkspaceIdentityOutcome.Bound;
}

/// <summary>The MOVE-versus-COPY decision (issue #317 D5/D6) as a PURE function, so the rule is
///     testable without a database or a file system and cannot be re-derived differently by a
///     second caller.
///     <para>The inputs are exactly the three facts a resolver can observe: the id written in
///     the directory's marker file, the path that id is bound to, and whether that bound path
///     still EXISTS. A bound path that is gone means the directory moved; a bound path that is
///     still there means this directory is a copy of it. That distinction is mechanical, which
///     is the point — the alternative is asking the user a question the harness can already
///     answer.</para></summary>
public static class WorkspaceIdentity
{
  /// <summary>Decides what to do, given <paramref name="markerId"/> (null when the directory
  ///     carries no marker), the directory being opened, the existing binding for that marker
  ///     id (null when unbound), and whether the bound root still exists. <paramref name="mintId"/>
  ///     supplies a fresh identity — injected so the rule is deterministic under test.</summary>
  public static WorkspaceIdentityResolution Decide(string? markerId, string root,
      WorkspaceIdentityBinding? binding, bool boundRootExists, Func<string> mintId)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(root);
    ArgumentNullException.ThrowIfNull(mintId);

    // No marker: a first open under #317, or a directory opened for the first time ever.
    // Either way the id is minted here and bound by the caller.
    if (string.IsNullOrWhiteSpace(markerId))
    {
      return new WorkspaceIdentityResolution(mintId(), WorkspaceIdentityOutcome.Bound, null);
    }

    string id = markerId.Trim();

    // A marker naming an identity nothing has bound yet: the row was lost (a pruned database,
    // a hand-copied marker). Binding it is the honest repair - the directory is the authority
    // on which identity it is.
    if (binding is null)
    {
      return new WorkspaceIdentityResolution(id, WorkspaceIdentityOutcome.Bound, null);
    }

    if (PathsEqual(binding.Root, root))
    {
      return new WorkspaceIdentityResolution(id, WorkspaceIdentityOutcome.Bound, null);
    }

    // The id is bound to a DIFFERENT path. Which of move and copy this is depends on whether
    // that other path is still there.
    return boundRootExists
      ? new WorkspaceIdentityResolution(mintId(), WorkspaceIdentityOutcome.Copied,
          $"[workspace: this directory is a copy of {binding.Root}, which still exists. It has been given its own identity, so its plans, chains, and state start empty here; {binding.Root} keeps the records.]")
      : new WorkspaceIdentityResolution(id, WorkspaceIdentityOutcome.Moved,
          $"[workspace: this directory moved from {binding.Root}. It keeps its identity, so its plans, chains, and state followed it.]");
  }

  /// <summary>Path comparison for workspace roots: case-insensitive and trailing-separator
  ///     blind, because Windows reports one directory both ways and a change of separator is
  ///     not a move.</summary>
  public static bool PathsEqual(string left, string right)
      => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

  /// <summary>The comparable form of a workspace root: trimmed of trailing separators.
  ///     Deliberately NOT full-path canonicalized - the resolver stores what the caller opened,
  ///     and two spellings of one directory compare equal here without inventing a third
  ///     spelling to store.</summary>
  public static string Normalize(string path)
      => string.IsNullOrWhiteSpace(path)
          ? string.Empty
          : path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

/// <summary>Resolves a directory's stable workspace identity (issue #317 D5/D6) by reading and
///     writing the marker file inside it. Implemented in the File System ACL, which owns file
///     access; the identity TABLE is a separate port (<see cref="IWorkspaceIdentityStore"/>) so the
///     rule and the storage can be tested apart.</summary>
public interface IWorkspaceIdentityResolver
{
  /// <summary>Resolves <paramref name="root"/>'s identity, binding it when it is new, re-binding
  ///     it when the directory moved, and minting a fresh one when it is a copy. The result
  ///     carries the notice a surface should show for the two noteworthy outcomes.</summary>
  Task<Result<WorkspaceIdentityResolution>> ResolveAsync(string root, CancellationToken ct = default);
}

/// <summary>Persistence port for workspace identity bindings (issue #317 D5). The implementation
///     lives in the Storage ACL; the resolver that reads the marker file lives in the File
///     System ACL. Expected failures flow as <see cref="DomainError"/>s through
///     <see cref="Result{T}"/> — never exceptions.</summary>
public interface IWorkspaceIdentityStore
{
  /// <summary>The binding for an identity, or null when nothing has bound it.</summary>
  Task<Result<WorkspaceIdentityBinding?>> FindByIdAsync(string id, CancellationToken ct = default);

  /// <summary>The binding for a directory path, or null when no identity claims it.</summary>
  Task<Result<WorkspaceIdentityBinding?>> FindByRootAsync(string root, CancellationToken ct = default);

  /// <summary>Binds an identity to a path, replacing any binding for EITHER side: a move
  ///     re-points one identity, and a copy takes a fresh identity that must not collide with
  ///     the path it was copied from. Idempotent for an unchanged pair.</summary>
  Task<Result<WorkspaceIdentityBinding>> BindAsync(string id, string root, CancellationToken ct = default);

  /// <summary>Every binding, newest first - what the orphan check walks to answer 'does this
  ///     chain's workspace still exist'.</summary>
  Task<Result<IReadOnlyList<WorkspaceIdentityBinding>>> ListAsync(CancellationToken ct = default);
}
