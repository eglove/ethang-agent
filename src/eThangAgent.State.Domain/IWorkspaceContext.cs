namespace eThangAgent.StateDomain;

/// <summary>The workspace an open session is rooted at, in the two forms a caller can need.
///     <para>BEFORE #317 THESE WERE ONE VALUE. The identity WAS the directory path, so a moved
///     workspace read as a brand-new one and every plan, chain, state key, and session under it
///     was orphaned. They are now separate facts because they answer different questions:</para>
///     <para><see cref="WorkspaceId"/> is the STABLE IDENTITY every scoped row keys on. It
///     survives a move, and nothing may interpret it - it is an opaque string (a path for a
///     workspace opened before #317, a minted id for one opened after).</para>
///     <para><see cref="WorkspaceRoot"/> is WHERE that identity currently lives, for the seams
///     that genuinely need a directory: path resolvers, the shell's working directory, the git
///     and snapshot seams, the computer-use broker. A seam that takes a PATH must be given this
///     one; a seam that takes a SCOPE must be given the id. Passing either for the other is the
///     defect this split exists to make visible - it used to compile because both were the same
///     string.</para></summary>
public interface IWorkspaceContext
{
  /// <summary>The stable workspace identity: the scope every durable store keys on.</summary>
  string WorkspaceId { get; }

  /// <summary>The absolute directory the identity is currently bound to.</summary>
  string WorkspaceRoot { get; }
}
