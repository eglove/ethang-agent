using eThangAgent.StateDomain;

namespace eThangAgent.Composition;

/// <summary>Constant workspace identity and root for a host that resolved them once at startup.
///     <para>Since #317 the two are DIFFERENT VALUES and both are required: the id is the scope
///     durable rows key on (stable across a move), the root is the directory the session works in.
///     A caller that supplies only a path is supplying both, which is what a pre-#317 workspace
///     is - its id IS its path - so <see cref="ForPath"/> exists for the test and legacy callers
///     that have nothing but a directory.</para></summary>
public sealed class FixedWorkspaceContext(string workspaceId, string workspaceRoot) : IWorkspaceContext
{
  /// <summary>The identity, or the path itself when no separate identity is known.</summary>
  public string WorkspaceId { get; } = Validate(workspaceId, nameof(workspaceId));

  /// <summary>The directory the session is rooted at.</summary>
  public string WorkspaceRoot { get; } = Validate(workspaceRoot, nameof(workspaceRoot));

  /// <summary>A context whose id IS its path: the pre-#317 shape, for a caller that has only a
  ///     directory (a test harness, a headless stub, a workspace that predates the identity
  ///     table). Explicitly named so the equivalence reads as a decision, not an oversight.</summary>
  public static FixedWorkspaceContext ForPath(string path) => new(path, path);

  /// <summary>A context for a caller that knows a workspace's IDENTITY but not, or not yet, its
  ///     directory (issue #285): the Open Workspace dialog writes a gate key for a workspace it
  ///     has resolved an id for, and no path-taking seam is involved in a key write.
  ///     <para>THE ROOT IS THE ID because there is nothing better to put there and a blank would
  ///     throw. That is honest rather than clever: a caller reaching the root from this context is
  ///     reaching for a directory it does not have, and the next reader should see that in the
  ///     one place it could happen. Every seam this context is built for takes a SCOPE.</para>
  ///     </summary>
  public static FixedWorkspaceContext ForIdentity(string workspaceId) => new(workspaceId, workspaceId);

  private static string Validate(string value, string name) => string.IsNullOrWhiteSpace(value)
      ? throw new ArgumentException("A workspace id and root must both be non-empty.", name)
      : value;
}
