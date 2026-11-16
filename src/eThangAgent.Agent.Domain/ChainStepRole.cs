namespace eThangAgent.AgentDomain;

/// <summary>What a chain step's session is FOR. The vocabulary is exactly three members:
///     <see cref="Implement"/>, <see cref="Review"/>, and <see cref="Refine"/>.
///     <para>TWO NON-IMPLEMENTATION ROLES, AND THEY DO DIFFERENT JOBS. <see cref="Refine"/>
///     works on the PLAN: it reads the plan, takes a second opinion on it, resolves what is
///     ambiguous, and sharpens each remaining step until it states what must be observably
///     true. <see cref="Review"/> works on the IMPLEMENTATION: it judges work that has already
///     been done, returns a verdict, and either advances the chain or sends the judged step
///     back with the findings that make it fixable. A plan is made executable by one and
///     judged as built by the other, so neither can stand in for the other.</para>
///     <para>ROLES ARE ASSIGNED, NOT EMERGENT (parent #299 D15). An assigned role is checkable
///     and an emergent one is not: there is a role on the record, a verdict the cursor reads
///     for a review step, a brief that differs per role, and a completion contract that differs
///     per role.</para>
///     <para>A ROLE CARRIES NO MODEL. A step's session runs on whatever model the workspace has
///     selected, exactly as every other session does; the role decides what the session is
///     asked to do, never which model is asked to do it.</para></summary>
public enum ChainStepRoleKind
{
  /// <summary>This step's session changes the workspace: it is the work.</summary>
  Implement,

  /// <summary>This step's session judges another step's implementation and returns a verdict
  ///     the cursor acts on: pass advances, fail sends the judged step back.</summary>
  Review,

  /// <summary>This step's session makes the plan executable and then advances: it refines the
  ///     plan's remaining steps rather than implementing them.</summary>
  Refine,
}

/// <summary>A chain step's assigned role: the kind, and nothing else.
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

  /// <summary>The reviewer role: the role whose brief asks for a verdict on the work.</summary>
  public static ChainStepRole Review { get; } = new(ChainStepRoleKind.Review);

  /// <summary>The refine role: the role whose brief asks for the plan to be made executable.
  ///     </summary>
  public static ChainStepRole Refine { get; } = new(ChainStepRoleKind.Refine);

  /// <summary>True for a review step: the step that judges work and returns a verdict.</summary>
  public bool IsReview => Kind == ChainStepRoleKind.Review;

  /// <summary>True for a refine step: the step whose work is ON THE PLAN rather than on the
  ///     workspace.</summary>
  public bool IsRefine => Kind == ChainStepRoleKind.Refine;

  /// <summary>The stored role name. The enum member name, so a CHECK constraint and a
  ///     reader agree without a second spelling to keep in sync.</summary>
  public string Encoded => Kind.ToString();

  /// <summary>Reads a persisted role. STRICT: only a DEFINED enum member parses, so a
  ///     corrupt column is reported rather than silently read as an implementer - a step
  ///     whose role nobody can name is worse than a named failure.
  ///     <para><c>Enum.TryParse</c> alone is not enough: it accepts a numeric string, and once
  ///     that number names a defined member it smuggles a role nobody wrote into the domain.
  ///     The stored encoding is the member NAME, so the name is what must round-trip.</para>
  ///     </summary>
  /// <param name="encoded">The stored role name.</param>
  /// <param name="role">The decoded role, or null.</param>
  public static bool TryDecode(string? encoded,
      [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ChainStepRole? role)
  {
    role = null;
    // THE ENCODING IS THE MEMBER NAME, so the name is what must come back: a numeric string
    // ('2') parses to a DEFINED member and would be silently accepted as a role nobody wrote,
    // which is the smuggle the strictness exists to refuse.
    if (string.IsNullOrWhiteSpace(encoded)
        || !Enum.TryParse(encoded, ignoreCase: false, out ChainStepRoleKind kind)
        || !Enum.IsDefined(kind)
        || !string.Equals(kind.ToString(), encoded, StringComparison.Ordinal))
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
