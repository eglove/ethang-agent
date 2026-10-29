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

  private static string Validate(string value, string name) => string.IsNullOrWhiteSpace(value)
      ? throw new ArgumentException("A workspace id and root must both be non-empty.", name)
      : value;
}
