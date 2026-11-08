namespace eThangAgent.AgentDomain;

/// <summary>What a chain step's session is FOR (issue #313 D7). The vocabulary is exactly two
///     members in v1: <see cref="Implement"/> and
///     <see cref="Review"/>.
///     <para>ROLES ARE ASSIGNED, NOT EMERGENT (parent #299 D15). An assigned role is checkable
///     and an emergent one is not: there is a role on the record, a verdict the cursor reads,
///     and a brief that can differ per role. That is a deliberate departure from Agensh,
///     recorded on the parent, and reopening it is a decision rather than an improvement.</para>
///     <para>TWO MEMBERS IS ENOUGH to express the loop the parent describes; more would be
///     speculation about workflows that do not exist. A chain with no review step is legal -
///     independent implementation work needs none (issue #306 D4).</para>
///     <para>A ROLE CARRIES NO MODEL. A step's session runs on whatever model the workspace has
///     selected, exactly as every other session does; the role decides what the session is
///     asked to do, never which model is asked to do it.</para>
///     <para>NO GRANT SET YET (D7). A role that carried capabilities would be a second
///     permission surface beside the spawn contract's, and nothing asks for one.</para></summary>
public enum ChainStepRoleKind
{
  /// <summary>This step's session changes the workspace: it is the work.</summary>
  Implement,

  /// <summary>This step's session judges another step's work and returns a verdict
  ///     (issue #306).</summary>
  Review,
}
/// <summary>A chain step's assigned role (issue #313 D7): the kind, and nothing else.
///     <para>IMMUTABLE AND TOTAL. Every chain step has a role - there is no 'unassigned'
///     state, because a step whose role nobody declared is a step nobody can start
///     correctly. <see cref="Implement"/> is the default the creation call applies when a
///     step names none, and that default is stated once, here, rather than at each call
///     site.</para>
///     <para>PERSISTED AS ONE COLUMN (<c>role</c>): the kind is a closed set a CHECK constraint
///     can hold.</para></summary>
/// <param name="Kind">Which role this step was assigned.</param>
public sealed record ChainStepRole(ChainStepRoleKind Kind)
{
  /// <summary>The implementer role: the default for a step that names none.</summary>
  public static ChainStepRole Implement { get; } = new(ChainStepRoleKind.Implement);

  /// <summary>The reviewer role.</summary>
  public static ChainStepRole Review { get; } = new(ChainStepRoleKind.Review);

  /// <summary>True for a review step: the role whose brief asks for a verdict rather than for
  ///     the work.</summary>
  public bool IsReview => Kind == ChainStepRoleKind.Review;

  /// <summary>The stored role name. The enum member name, so a CHECK constraint and a
  ///     reader agree without a second spelling to keep in sync.</summary>
  public string Encoded => Kind.ToString();
  /// <summary>Reads a persisted role. STRICT: only a DEFINED enum member parses, so a
  ///     corrupt column is reported rather than silently read as an implementer - a step
  ///     whose role nobody can name is worse than a named failure.
  ///     <para><c>Enum.TryParse</c> alone is not enough: it accepts a numeric string ('5')
  ///     and yields an UNDEFINED member, which would smuggle a value outside the enum into
  ///     the domain (the <c>SqliteChainStore</c> status rule, applied here for the same
  ///     reason).</para></summary>
  /// <param name="encoded">The stored role name.</param>
  /// <param name="role">The decoded role, or null.</param>
  public static bool TryDecode(string? encoded,
      [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ChainStepRole? role)
  {
    role = null;
    if (string.IsNullOrWhiteSpace(encoded)
        || !Enum.TryParse(encoded, ignoreCase: false, out ChainStepRoleKind kind)
        || !Enum.IsDefined(kind))
    {
      return false;
    }

    role = new ChainStepRole(kind);
    return true;
  }

  public override string ToString() => Encoded;
}
/// <summary>One step a chain is created from (issue #313 D2): the plan position it executes
///     and the role assigned to the session that will run it.
///     <para>A SEED, NOT A <see cref="ChainStep"/>: a chain step carries execution state
///     (status, attempts, claim clock) that does not exist until the record does. The
///     creation call states the two facts it actually knows.</para></summary>
/// <param name="Position">The 1-based plan step position this chain step executes.</param>
/// <param name="Role">The role assigned to the session that runs it.</param>
/// <param name="Group">The group the step runs in, or null when it runs alone. A group id is
///     an integer of at least 1, assigned by the caller; it is not a position and carries no
///     ordering meaning.</param>
public sealed record ChainStepSeed(int Position, ChainStepRole Role, int? Group = null)
{
  /// <summary>An implementer seed for <paramref name="position"/>: the common case, stated
  ///     once so a caller that assigns no role does not re-derive the default.</summary>
  /// <param name="position">The 1-based plan step position.</param>
  public static ChainStepSeed Implementing(int position) =>
      new(position, ChainStepRole.Implement);
}
